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
        (nint value, ManagedTupleCustomTypeInfo? tupleCustomTypeInfo) = ResolveFrameValue(
            frame, name, allowInstanceReceiver: true);
        try
        {
            return RetainExpressionValue(
                name, name, value, frame.Id, generation, tupleCustomTypeInfo);
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
        ManagedTupleCustomTypeInfo? tupleCustomTypeInfo)
    {
        (ManagedValueDisplay runtimeValue, ManagedValueDisplay formatted) = FormatRuntimeValuePair(
            value, debuggerDisplayDepth: 0, tupleCustomTypeInfo);
        ManagedValueReferences references = RetainValue(
            value, generation, evaluateName, frameId, tupleCustomTypeInfo: tupleCustomTypeInfo);
        ManagedExpressionValue expression = ManagedExpressionValueFactory.FromVariable(
            new DebugVariableInfo(
                name, formatted.Value, formatted.Type,
                references.VariablesReference, references.MemoryReference, evaluateName),
            runtimeValueReference: 0,
            runtimeValue);
        if (expression.HasScalar && expression.Scalar is not string)
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
            tupleCustomTypeInfo);
        return expression with { RuntimeValueReference = retained.Id };
    }

    private ManagedTupleCustomTypeInfo? GetExpressionTupleCustomTypeInfo(
        ManagedExpressionValue value) =>
        _values.TryGetValue(value.RuntimeValueReference, out ManagedValueHandle? retained)
            ? retained.TupleCustomTypeInfo
            : null;

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
