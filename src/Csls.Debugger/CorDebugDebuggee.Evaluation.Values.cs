using Csls.Debugger.Contracts;

namespace Csls.Debugger;

/// <summary>
/// Retains expression runtime identities independently of debugger presentation handles.
/// </summary>
internal sealed partial class CorDebugDebuggee
{
    private ManagedExpressionValue EvaluateRuntimeRoot(
        ManagedFrameHandle frame,
        string name,
        DebugStopGeneration generation)
    {
        (nint value, ManagedTupleCustomTypeInfo? tupleCustomTypeInfo, ManagedValueOrigin? origin, ManagedBoundType? declaredType) = ResolveFrameValue(
            frame, name, allowInstanceReceiver: true);
        try
        {
            return RetainExpressionValue(
                name, name, value, frame.Id, generation, tupleCustomTypeInfo, origin, declaredType);
        }
        finally
        {
            ReleaseFunctionEvaluationPointer(value);
        }
    }

    private ManagedExpressionValue RetainExpressionValue(
        string name,
        string? evaluateName,
        nint value,
        int frameId,
        DebugStopGeneration generation,
        ManagedTupleCustomTypeInfo? tupleCustomTypeInfo,
        ManagedValueOrigin? origin,
        ManagedBoundType? declaredType = null)
    {
        (ManagedValueDisplay runtimeValue, ManagedValueDisplay formatted) = FormatRuntimeValuePair(
            value, debuggerDisplayDepth: 0, tupleCustomTypeInfo);
        ManagedValueReferences references = RetainValue(
            value, generation, evaluateName, frameId,
            tupleCustomTypeInfo: tupleCustomTypeInfo, origin: origin);
        ManagedExpressionValue expression = ManagedExpressionValueFactory.FromVariable(
            new DebugVariableInfo(
                name, formatted.Value, formatted.Type,
                references.VariablesReference, references.MemoryReference, evaluateName),
            runtimeValueReference: 0,
            runtimeValue) with
        { DeclaredType = declaredType };
        if (expression.HasScalar && expression.Scalar is not string &&
            ManagedRuntimeValueIdentity.GetElementType(value) is not (0x11 or 0x0e or 0x12 or 0x14 or 0x1c or 0x1d))
        {
            return expression;
        }

        ManagedValueHandle retained = RetainRuntimeValue(
            value,
            generation,
            evaluateName,
            frameId,
            GetValueThreadId(frameId),
            ManagedValueView.Default,
            tupleCustomTypeInfo,
            origin);
        return expression with { RuntimeValueReference = retained.Id };
    }

    private ManagedTupleCustomTypeInfo? GetExpressionTupleCustomTypeInfo(
        ManagedExpressionValue value)
    {
        if (value.RuntimeValueReference <= 0)
        {
            return null;
        }

        if (!_values.TryGetValue(value.RuntimeValueReference, out ManagedValueHandle? retained))
        {
            throw new InvalidOperationException(
                $"Variable reference {value.RuntimeValueReference} is stale or unknown.");
        }

        ValidateValueLifetime(retained);
        return retained.TupleCustomTypeInfo;
    }

    private int[] EvaluateArrayIndexes(
        ManagedFrameHandle frame,
        DebugExpressionPlan plan,
        DebugExpressionNode node,
        DebugStopGeneration generation)
    {
        int[] indexes = new int[node.Children.Count - 1];
        for (int index = 0; index < indexes.Length; index++)
        {
            indexes[index] = ManagedExpressionValueFactory.RequireArrayIndex(EvaluateNode(
                frame, plan, node.Children[index + 1], generation));
        }

        return indexes;
    }
}
