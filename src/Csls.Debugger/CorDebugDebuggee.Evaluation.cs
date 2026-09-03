using Csls.Debugger.Contracts;

namespace Csls.Debugger;

/// <summary>
/// Binds language-neutral expression plans against generation-bound managed frames.
/// </summary>
internal sealed partial class CorDebugDebuggee
{
    /// <summary>
    /// Gets the source-language grammar recorded for one managed frame.
    /// </summary>
    /// <param name="frameId">The session-local frame handle.</param>
    /// <param name="generation">The current debugger stop generation.</param>
    /// <returns>The evaluator language selected from the frame symbols.</returns>
    internal DebugExpressionLanguage GetExpressionLanguage(
        int frameId,
        DebugStopGeneration generation) => GetFrame(frameId, generation).ExpressionLanguage;

    /// <summary>
    /// Resolves a safe expression plan without executing code in the target process.
    /// </summary>
    /// <param name="frameId">The session-local frame handle.</param>
    /// <param name="plan">The compiler-bound language-neutral expression plan.</param>
    /// <param name="generation">The stop generation that owns the frame.</param>
    /// <returns>The formatted current-generation expression value.</returns>
    internal DebugEvaluateResult Evaluate(
        int frameId,
        DebugExpressionPlan plan,
        DebugStopGeneration generation)
    {
        ManagedFrameHandle frame = GetFrame(frameId, generation);
        ManagedExpressionPlanValidator.Validate(plan, frame.ExpressionLanguage);
        return EvaluateNode(frame, plan, plan.Root, generation).ToResult();
    }

    private ManagedExpressionValue EvaluateNode(
        ManagedFrameHandle frame,
        DebugExpressionPlan plan,
        DebugExpressionNode node,
        DebugStopGeneration generation) => node.Kind switch
        {
            DebugExpressionNodeKind.Identifier => ManagedExpressionValueFactory.FromVariable(
                ResolveRoot(frame, node.Text!, generation)),
            DebugExpressionNodeKind.This => ManagedExpressionValueFactory.FromVariable(
                ResolveRoot(frame, "this", generation)),
            DebugExpressionNodeKind.Literal => ManagedExpressionValueFactory.FromLiteral(node),
            DebugExpressionNodeKind.MemberAccess => EvaluateMember(
                frame,
                plan,
                node,
                generation),
            DebugExpressionNodeKind.ElementAccess => EvaluateElement(
                frame,
                plan,
                node,
                generation),
            DebugExpressionNodeKind.Unary => ManagedPrimitiveOperatorEvaluator.EvaluateUnary(
                node.Operator,
                EvaluateNode(frame, plan, node.Children[0], generation)),
            DebugExpressionNodeKind.Binary => EvaluateBinary(
                frame,
                plan,
                node,
                generation),
            DebugExpressionNodeKind.Conditional => EvaluateConditional(
                frame,
                plan,
                node,
                generation),
            _ => throw new InvalidDataException(
                $"Expression node kind {node.Kind} is not supported.")
        };

    private ManagedExpressionValue EvaluateMember(
        ManagedFrameHandle frame,
        DebugExpressionPlan plan,
        DebugExpressionNode node,
        DebugStopGeneration generation)
    {
        ManagedExpressionValue receiver = EvaluateNode(
            frame,
            plan,
            node.Children[0],
            generation);
        if (receiver.Display.VariablesReference == 0)
        {
            throw new InvalidOperationException(
                $"'{receiver.Display.EvaluateName ?? receiver.Display.Name}' has no " +
                "expandable children.");
        }

        StringComparison comparison = plan.Language == DebugExpressionLanguage.VisualBasic
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        DebugVariableInfo? member = GetVariables(
            receiver.Display.VariablesReference,
            generation,
            start: 0,
            count: 0).FirstOrDefault(candidate => string.Equals(
                candidate.Name,
                node.Text,
                comparison));
        return member is null
            ? throw new InvalidOperationException(
                $"The expression member '{node.Text}' is unavailable.")
            : ManagedExpressionValueFactory.FromVariable(member);
    }

    private ManagedExpressionValue EvaluateElement(
        ManagedFrameHandle frame,
        DebugExpressionPlan plan,
        DebugExpressionNode node,
        DebugStopGeneration generation)
    {
        ManagedExpressionValue receiver = EvaluateNode(
            frame,
            plan,
            node.Children[0],
            generation);
        if (receiver.Display.VariablesReference == 0)
        {
            throw new InvalidOperationException(
                $"'{receiver.Display.EvaluateName ?? receiver.Display.Name}' is not an " +
                "expandable array.");
        }

        int[] indexes = new int[node.Children.Count - 1];
        for (int index = 0; index < indexes.Length; index++)
        {
            indexes[index] = ManagedExpressionValueFactory.RequireArrayIndex(EvaluateNode(
                frame,
                plan,
                node.Children[index + 1],
                generation));
        }

        string name = $"[{string.Join(',', indexes)}]";
        DebugVariableInfo? element = GetVariables(
            receiver.Display.VariablesReference,
            generation,
            start: 0,
            count: 0).FirstOrDefault(candidate => string.Equals(
                candidate.Name,
                name,
                StringComparison.Ordinal));
        return element is null
            ? throw new InvalidOperationException(
                $"The expression array index '{name}' is unavailable.")
            : ManagedExpressionValueFactory.FromVariable(element);
    }

    private ManagedExpressionValue EvaluateBinary(
        ManagedFrameHandle frame,
        DebugExpressionPlan plan,
        DebugExpressionNode node,
        DebugStopGeneration generation)
    {
        ManagedExpressionValue left = EvaluateNode(
            frame,
            plan,
            node.Children[0],
            generation);
        if (node.Operator == DebugExpressionOperator.LogicalAnd &&
            !ManagedExpressionValueFactory.RequireBoolean(left))
        {
            return ManagedExpressionValueFactory.FromScalar(value: false, "bool");
        }

        if (node.Operator == DebugExpressionOperator.LogicalOr &&
            ManagedExpressionValueFactory.RequireBoolean(left))
        {
            return ManagedExpressionValueFactory.FromScalar(value: true, "bool");
        }

        ManagedExpressionValue right = EvaluateNode(
            frame,
            plan,
            node.Children[1],
            generation);
        if (node.Operator is DebugExpressionOperator.LogicalAnd or
            DebugExpressionOperator.LogicalOr)
        {
            return ManagedExpressionValueFactory.FromScalar(
                ManagedExpressionValueFactory.RequireBoolean(right),
                "bool");
        }

        return ManagedPrimitiveOperatorEvaluator.EvaluateBinary(node.Operator, left, right);
    }

    private ManagedExpressionValue EvaluateConditional(
        ManagedFrameHandle frame,
        DebugExpressionPlan plan,
        DebugExpressionNode node,
        DebugStopGeneration generation)
    {
        bool condition = ManagedExpressionValueFactory.RequireBoolean(EvaluateNode(
            frame,
            plan,
            node.Children[0],
            generation));
        return EvaluateNode(
            frame,
            plan,
            node.Children[condition ? 1 : 2],
            generation);
    }

    private DebugVariableInfo ResolveRoot(
        ManagedFrameHandle frame,
        string rootName,
        DebugStopGeneration generation)
    {
        StringComparison comparison = frame.ExpressionLanguage ==
            DebugExpressionLanguage.VisualBasic
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal;
        DebugVariableInfo? local = EnumerateValues(
            frame.Pointer,
            ManagedScopeKind.Locals,
            GetVariableNames(frame, ManagedScopeKind.Locals),
            generation,
            start: 0,
            count: 0).FirstOrDefault(candidate => string.Equals(
                candidate.Name,
                rootName,
                comparison));
        if (local is not null)
        {
            return local;
        }

        DebugVariableInfo? argument = EnumerateValues(
            frame.Pointer,
            ManagedScopeKind.Arguments,
            GetVariableNames(frame, ManagedScopeKind.Arguments),
            generation,
            start: 0,
            count: 0).FirstOrDefault(candidate => string.Equals(
                candidate.Name,
                rootName,
                comparison));
        return argument ?? throw new InvalidOperationException(
            $"The expression root '{rootName}' is unavailable in the selected frame.");
    }
}
