using Csls.Debugger.Contracts;
using Csls.Debugger.Interop;

namespace Csls.Debugger;

/// <summary>
/// Resolves writable managed array elements from source-language indexes.
/// </summary>
internal sealed partial class CorDebugDebuggee
{
    private ManagedAssignmentTarget ResolveArrayAssignmentTarget(
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
        string? evaluateName = ManagedExpressionName.CreateElement(
            receiver.Display.EvaluateName, indexes, plan.Language);
        ManagedTupleCustomTypeInfo? tupleCustomTypeInfo = GetExpressionTupleCustomTypeInfo(receiver);
        (nint value, ManagedValueOrigin? origin) = ResolveArrayElementValue(receiver, indexes);
        return ManagedAssignmentTarget.TakeOwnership((value, tupleCustomTypeInfo, origin), evaluateName);
    }

    private unsafe (nint Value, ManagedValueOrigin? Origin) ResolveArrayElementValue(
        ManagedExpressionValue receiver,
        int[] indexes)
    {
        nint runtimeValue = GetRuntimeValue(receiver);
        nint dereferenced = 0;
        nint array = 0;
        nint element = 0;
        try
        {
            dereferenced = DereferenceValue(runtimeValue);
            array = ComAbi.QueryInterface(
                dereferenced,
                ICorDebugArrayValueAbi.InterfaceId);
            var api = new ICorDebugArrayValueAbi(array);
            uint rank = GetArrayRank(api);
            if (indexes.Length != rank)
            {
                throw new InvalidOperationException(
                    $"The expression supplies {indexes.Length} array index(es), " +
                    $"but the runtime array rank is {rank}.");
            }

            uint[] dimensions = GetArrayDimensions(api, rank);
            int[] bases = GetArrayBases(api, rank);
            uint position = 0;
            for (int index = 0; index < dimensions.Length; index++)
            {
                int sourceIndex = indexes[index];
                long offset = (long)sourceIndex - bases[index];
                if (offset < 0 || offset >= dimensions[index])
                {
                    throw new InvalidOperationException(
                        $"Array index {sourceIndex} is outside dimension {index}'s bounds.");
                }

                position = checked(position * dimensions[index] + (uint)offset);
            }

            ManagedValueOrigin? parentOrigin = GetValueOrigin(receiver);
            ManagedValueOrigin? origin = parentOrigin is null
                ? null
                : new ManagedArrayElementValueOrigin(parentOrigin, checked((int)position));
            nint* elementAddress = &element;
            CorDebugHResult.ThrowIfFailed(
                api.GetElementAtPosition(position, (nint)elementAddress),
                "ICorDebugArrayValue.GetElementAtPosition");

            element = RequirePointer(
                Volatile.Read(ref *elementAddress),
                "ICorDebugArrayValue.GetElementAtPosition");
            return (element, origin);
        }
        catch (OverflowException exception)
        {
            throw new InvalidOperationException(
                "The managed array index exceeds the supported element range.",
                exception);
        }
        finally
        {
            if (array != 0)
            {
                _ = ComAbi.Release(array);
            }

            if (dereferenced != 0)
            {
                _ = ComAbi.Release(dereferenced);
            }
        }
    }
}
