using Csls.Debugger.Contracts;
using Csls.Debugger.Interop;

namespace Csls.Debugger;

/// <summary>
/// Applies generation-safe writes to managed locals, arguments, fields, and arrays.
/// </summary>
internal sealed partial class CorDebugDebuggee
{
    /// <summary>
    /// Assigns a side-effect-free value plan to one writable source expression.
    /// </summary>
    /// <param name="frameId">The logical managed frame identifier for the visible stop.</param>
    /// <param name="target">The compiler-lowered writable target.</param>
    /// <param name="value">The compiler-lowered value expression.</param>
    /// <param name="targetExpression">The canonical source target expression.</param>
    /// <param name="resultName">The debugger-facing result name.</param>
    /// <param name="generation">The current stopped generation.</param>
    /// <returns>The written value and its current expansion handles.</returns>
    internal DebugVariableInfo SetExpression(
        int frameId,
        DebugExpressionPlan target,
        DebugExpressionPlan value,
        string targetExpression,
        string resultName,
        DebugStopGeneration generation)
    {
        ManagedFrameHandle frame = GetFrame(frameId, generation);
        ManagedExpressionPlanValidator.Validate(target, frame.ExpressionLanguage);
        ManagedExpressionPlanValidator.Validate(value, frame.ExpressionLanguage);
        if (value.Root.Kind is DebugExpressionNodeKind.Invocation or
            DebugExpressionNodeKind.ObjectCreation)
        {
            throw new InvalidOperationException(
                "Assignment values cannot execute target code.");
        }

        ManagedExpressionValue source = EvaluateNode(
            frame,
            value,
            value.Root,
            generation);
        return SetExpressionCore(
            frame,
            target,
            source,
            targetExpression,
            resultName,
            generation,
            value.Language,
            value.Root.Kind == DebugExpressionNodeKind.Literal);
    }

    /// <summary>
    /// Creates a string-materialization plan when safe evaluation produced no runtime reference.
    /// </summary>
    /// <param name="frameId">The logical managed frame identifier for the visible stop.</param>
    /// <param name="value">The compiler-lowered value expression.</param>
    /// <param name="generation">The current stopped generation.</param>
    /// <returns>A literal string plan to materialize, or null when direct assignment is sufficient.</returns>
    internal DebugExpressionPlan? CreateStringMaterializationPlan(
        int frameId,
        DebugExpressionPlan value,
        DebugStopGeneration generation)
    {
        ManagedFrameHandle frame = GetFrame(frameId, generation);
        ManagedExpressionPlanValidator.Validate(value, frame.ExpressionLanguage);
        ManagedExpressionValue source = EvaluateNode(
            frame,
            value,
            value.Root,
            generation);
        return source is
        {
            HasScalar: true,
            Scalar: string text,
            RuntimeValueReference: <= 0
        }
            ? new DebugExpressionPlan(
                DebuggerEvaluatorProtocol.CurrentPlanVersion,
                value.Language,
                new DebugExpressionNode(
                    DebugExpressionNodeKind.Literal,
                    DebugExpressionOperator.None,
                    text,
                    "string",
                    []))
            : null;
    }

    /// <summary>
    /// Assigns a retained function-evaluation result to a freshly reacquired target.
    /// </summary>
    /// <param name="frameId">The logical managed frame reacquired in the replacement generation.</param>
    /// <param name="target">The compiler-lowered writable target.</param>
    /// <param name="evaluation">The retained target-code result.</param>
    /// <param name="targetExpression">The canonical source target expression.</param>
    /// <param name="resultName">The debugger-facing result name.</param>
    /// <returns>The written value and its current expansion handles.</returns>
    internal DebugVariableInfo SetExpressionFromEvaluation(
        int frameId,
        DebugExpressionPlan target,
        ManagedFunctionEvaluationResult evaluation,
        string targetExpression,
        string resultName)
    {
        if (evaluation.RuntimeValueReference <= 0 ||
            !_values.TryGetValue(
                evaluation.RuntimeValueReference,
                out ManagedValueHandle? retained) ||
            retained.Generation != evaluation.Generation)
        {
            throw new InvalidOperationException(
                "The assignment evaluation did not produce a retained runtime value.");
        }

        ValidateValueLifetime(retained);
        ManagedFrameHandle frame = GetFrame(frameId, evaluation.Generation);
        ManagedExpressionPlanValidator.Validate(target, frame.ExpressionLanguage);
        ManagedExpressionValue source = ManagedExpressionValueFactory.FromVariable(
            new DebugVariableInfo(
                "$result",
                evaluation.Result.Result,
                evaluation.Result.Type,
                evaluation.Result.VariablesReference,
                evaluation.Result.MemoryReference,
                EvaluateName: null),
            evaluation.RuntimeValueReference,
            FormatRuntimeValuePair(
                retained.Pointer, debuggerDisplayDepth: 0, retained.TupleCustomTypeInfo).Runtime);
        return SetExpressionCore(
            frame,
            target,
            source,
            targetExpression,
            resultName,
            evaluation.Generation,
            target.Language,
            sourceIsContextualLiteral: false);
    }

    private DebugVariableInfo SetExpressionCore(
        ManagedFrameHandle frame,
        DebugExpressionPlan target,
        ManagedExpressionValue source,
        string targetExpression,
        string resultName,
        DebugStopGeneration generation,
        DebugExpressionLanguage language,
        bool sourceIsContextualLiteral)
    {
        nint destination = ResolveAssignmentTarget(
            frame,
            target,
            target.Root,
            generation);
        try
        {
            RetireResultsViewSnapshot();
            AssignManagedValue(
                destination,
                source,
                language,
                sourceIsContextualLiteral);
            ManagedValueDisplay display = FormatRuntimeValue(destination);
            ManagedValueReferences references = RetainValue(
                destination,
                generation,
                targetExpression,
                frame.Id);
            return new DebugVariableInfo(
                resultName,
                display.Value,
                display.Type,
                references.VariablesReference,
                references.MemoryReference,
                targetExpression);
        }
        finally
        {
            _ = ComAbi.Release(destination);
        }
    }

    private nint ResolveAssignmentTarget(
        ManagedFrameHandle frame,
        DebugExpressionPlan plan,
        DebugExpressionNode node,
        DebugStopGeneration generation) => node.Kind switch
        {
            DebugExpressionNodeKind.Identifier => ResolveFrameAssignmentTarget(
                frame,
                node.Text!),
            DebugExpressionNodeKind.MemberAccess => ResolveFieldAssignmentTarget(
                frame,
                plan,
                node,
                generation),
            DebugExpressionNodeKind.ElementAccess => ResolveArrayAssignmentTarget(
                frame,
                plan,
                node,
                generation),
            DebugExpressionNodeKind.This => throw new InvalidOperationException(
                "The current instance receiver cannot be assigned."),
            _ => throw new InvalidOperationException(
                $"Expression node {node.Kind} is not a writable managed value.")
        };

    private static nint ResolveFrameAssignmentTarget(
        ManagedFrameHandle frame,
        string name) => ResolveFrameValue(frame, name, allowInstanceReceiver: false).Value;

    private static (
        nint Value,
        ManagedTupleCustomTypeInfo? TupleCustomTypeInfo,
        ManagedValueOrigin? Origin) ResolveFrameValue(
        ManagedFrameHandle frame,
        string name,
        bool allowInstanceReceiver)
    {
        StringComparison comparison = frame.ExpressionLanguage ==
            DebugExpressionLanguage.VisualBasic
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal;
        IReadOnlyDictionary<int, ManagedSymbolVariable> localNames = GetVariableNames(
            frame,
            ManagedScopeKind.Locals);
        int? localIndex = FindVariableIndex(localNames, name, comparison);
        if (localIndex is not null)
        {
            return (
                GetFrameAssignmentTarget(
                    frame.Pointer, ManagedScopeKind.Locals, localIndex.Value),
                localNames[localIndex.Value].TupleCustomTypeInfo,
                frame.CreateValueOrigin(ManagedScopeKind.Locals, localIndex.Value));
        }

        IReadOnlyDictionary<int, ManagedSymbolVariable> argumentNames = GetVariableNames(
            frame,
            ManagedScopeKind.Arguments);
        int? argumentIndex = FindVariableIndex(argumentNames, name, comparison);
        if (argumentIndex is not null)
        {
            if (!allowInstanceReceiver &&
                (string.Equals(name, "this", comparison) ||
                string.Equals(name, "Me", comparison)))
            {
                throw new InvalidOperationException(
                    "The current instance receiver cannot be assigned.");
            }

            return (
                GetFrameAssignmentTarget(
                    frame.Pointer, ManagedScopeKind.Arguments, argumentIndex.Value),
                argumentNames[argumentIndex.Value].TupleCustomTypeInfo,
                frame.CreateValueOrigin(ManagedScopeKind.Arguments, argumentIndex.Value));
        }

        throw new InvalidOperationException(
            $"The assignment target '{name}' is unavailable in the selected frame.");
    }

    private static int? FindVariableIndex(
        IReadOnlyDictionary<int, ManagedSymbolVariable> names,
        string name,
        StringComparison comparison)
    {
        int? result = null;
        foreach ((int index, ManagedSymbolVariable candidate) in names)
        {
            if (!string.Equals(candidate.Name, name, comparison))
            {
                continue;
            }

            if (result is not null)
            {
                throw new InvalidOperationException(
                    $"Variable name '{name}' is ambiguous in the selected frame.");
            }

            result = index;
        }

        return result;
    }

    private static unsafe nint GetFrameAssignmentTarget(
        nint frame,
        ManagedScopeKind kind,
        int index)
    {
        nint ilFrame = 0;
        nint value = 0;
        try
        {
            ilFrame = ComAbi.QueryInterface(frame, ICorDebugILFrameAbi.InterfaceId);
            nint* valueAddress = &value;
            var api = new ICorDebugILFrameAbi(ilFrame);
            int result = kind == ManagedScopeKind.Arguments
                ? api.GetArgument(checked((uint)index), (nint)valueAddress)
                : api.GetLocalVariable(checked((uint)index), (nint)valueAddress);
            CorDebugHResult.ThrowIfFailed(result, $"ICorDebugILFrame.Get{kind}");
            value = RequirePointer(
                Volatile.Read(ref *valueAddress),
                $"ICorDebugILFrame.Get{kind}");
            return value;
        }
        finally
        {
            if (ilFrame != 0)
            {
                _ = ComAbi.Release(ilFrame);
            }
        }
    }
}
