using Csls.Debugger.Contracts;
using Csls.Debugger.Interop;

namespace Csls.Debugger;

/// <summary>
/// Resolves writable managed array elements from source-language indexes.
/// </summary>
internal sealed partial class CorDebugDebuggee
{
    private unsafe nint ResolveArrayAssignmentTarget(
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
            if (node.Children.Count - 1 != rank)
            {
                throw new InvalidOperationException(
                    $"The assignment supplies {node.Children.Count - 1} array index(es), " +
                    $"but the runtime array rank is {rank}.");
            }

            uint[] indexes = new uint[checked((int)rank)];
            for (int index = 0; index < indexes.Length; index++)
            {
                int sourceIndex = ManagedExpressionValueFactory.RequireArrayIndex(EvaluateNode(
                    frame,
                    plan,
                    node.Children[index + 1],
                    generation));
                indexes[index] = checked((uint)sourceIndex);
            }

            nint* elementAddress = &element;
            fixed (uint* indexesAddress = indexes)
            {
                CorDebugHResult.ThrowIfFailed(
                    api.GetElement(rank, (nint)indexesAddress, (nint)elementAddress),
                    "ICorDebugArrayValue.GetElement");
            }

            element = RequirePointer(
                Volatile.Read(ref *elementAddress),
                "ICorDebugArrayValue.GetElement");
            nint resolved = element;
            element = 0;
            return resolved;
        }
        catch (OverflowException exception)
        {
            throw new InvalidOperationException(
                "Managed array assignment indexes must be non-negative UInt32 values.",
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
