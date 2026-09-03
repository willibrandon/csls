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
    /// <param name="frameId">The generation-bound managed frame.</param>
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
        if (value.Root.Kind == DebugExpressionNodeKind.Invocation)
        {
            throw new InvalidOperationException(
                "Assignment values cannot execute target code.");
        }

        nint destination = ResolveAssignmentTarget(
            frame,
            target,
            target.Root,
            generation);
        try
        {
            ManagedExpressionValue source = EvaluateNode(
                frame,
                value,
                value.Root,
                generation);
            AssignManagedValue(
                destination,
                source,
                value.Language,
                value.Root.Kind == DebugExpressionNodeKind.Literal);
            ManagedValueDisplay display = CorDebugValueFormatter.Format(destination);
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

    private static unsafe nint ResolveFrameAssignmentTarget(
        ManagedFrameHandle frame,
        string name)
    {
        StringComparison comparison = frame.ExpressionLanguage ==
            DebugExpressionLanguage.VisualBasic
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal;
        IReadOnlyDictionary<int, string> localNames = GetVariableNames(
            frame,
            ManagedScopeKind.Locals);
        int? localIndex = FindVariableIndex(localNames, name, comparison);
        if (localIndex is not null)
        {
            return GetFrameAssignmentTarget(
                frame.Pointer,
                ManagedScopeKind.Locals,
                localIndex.Value);
        }

        IReadOnlyDictionary<int, string> argumentNames = GetVariableNames(
            frame,
            ManagedScopeKind.Arguments);
        int? argumentIndex = FindVariableIndex(argumentNames, name, comparison);
        if (argumentIndex is not null)
        {
            if (string.Equals(name, "this", comparison) ||
                string.Equals(name, "Me", comparison))
            {
                throw new InvalidOperationException(
                    "The current instance receiver cannot be assigned.");
            }

            return GetFrameAssignmentTarget(
                frame.Pointer,
                ManagedScopeKind.Arguments,
                argumentIndex.Value);
        }

        throw new InvalidOperationException(
            $"The assignment target '{name}' is unavailable in the selected frame.");
    }

    private static int? FindVariableIndex(
        IReadOnlyDictionary<int, string> names,
        string name,
        StringComparison comparison)
    {
        int? result = null;
        foreach ((int index, string candidate) in names)
        {
            if (!string.Equals(candidate, name, comparison))
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
            nint resolved = value;
            value = 0;
            return resolved;
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
