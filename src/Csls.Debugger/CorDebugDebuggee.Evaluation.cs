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

    /// <summary>
    /// Resolves a safe expression plan and requires a Boolean result.
    /// </summary>
    /// <param name="frameId">The session-local frame handle.</param>
    /// <param name="plan">The compiler-bound language-neutral expression plan.</param>
    /// <param name="generation">The stop generation that owns the frame.</param>
    /// <returns>The Boolean condition result.</returns>
    internal bool EvaluateCondition(
        int frameId,
        DebugExpressionPlan plan,
        DebugStopGeneration generation)
    {
        ManagedFrameHandle frame = GetFrame(frameId, generation);
        ManagedExpressionPlanValidator.Validate(plan, frame.ExpressionLanguage);
        return ManagedExpressionValueFactory.RequireBoolean(EvaluateNode(
            frame,
            plan,
            plan.Root,
            generation));
    }

    private ManagedExpressionValue EvaluateNode(
        ManagedFrameHandle frame,
        DebugExpressionPlan plan,
        DebugExpressionNode node,
        DebugStopGeneration generation) => node.Kind switch
        {
            DebugExpressionNodeKind.Identifier => EvaluateRuntimeRoot(
                frame, node.Text!, generation),
            DebugExpressionNodeKind.This => EvaluateRuntimeRoot(
                frame, "this", generation),
            DebugExpressionNodeKind.Literal => ManagedExpressionValueFactory.FromLiteral(node),
            DebugExpressionNodeKind.Conversion =>
                ManagedPrimitiveConversionEvaluator.EvaluateExplicit(
                    EvaluateNode(frame, plan, node.Children[0], generation),
                    node.TypeName!,
                    plan.Language),
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
        (nint value, ManagedTupleCustomTypeInfo? tupleCustomTypeInfo, ManagedValueOrigin? origin) = ResolveInstanceFieldValue(
            receiver, node.Text!, plan.Language);
        try
        {
            return RetainExpressionValue(
                node.Text!,
                ManagedExpressionName.CreateMember(receiver.Display.EvaluateName, node.Text!),
                value, frame.Id, generation, tupleCustomTypeInfo, origin);
        }
        finally
        {
            ReleaseFunctionEvaluationPointer(value);
        }
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
        int[] indexes = EvaluateArrayIndexes(frame, plan, node, generation);
        string name = $"[{string.Join(',', indexes)}]";
        string? evaluateName = receiver.Display.EvaluateName is string parent
            ? string.Concat(parent, name)
            : null;
        (nint value, ManagedValueOrigin? origin) = ResolveArrayElementValue(receiver, indexes);
        try
        {
            return RetainExpressionValue(
                name, evaluateName, value, frame.Id, generation,
                GetExpressionTupleCustomTypeInfo(receiver), origin);
        }
        finally
        {
            ReleaseFunctionEvaluationPointer(value);
        }
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

}
