using Csls.Debugger.Interop;

namespace Csls.Debugger;

/// <summary>
/// Owns completed enumeration snapshots and matches receivers without executing target code.
/// </summary>
internal sealed partial class CorDebugDebuggee
{
    private static void ValidateValueLifetime(ManagedValueHandle handle)
    {
        if (handle.Lifetime?.IsRetired == true)
        {
            throw new InvalidOperationException(
                $"Variable reference {handle.Id} belongs to a stale Results View snapshot.");
        }
    }

    private void RetireResultsViewSnapshot()
    {
        ManagedResultsViewSnapshot? snapshot = _resultsViewSnapshot;
        _resultsViewSnapshot = null;
        if (snapshot is not null)
        {
            snapshot.Lifetime.Retire();
            ReleaseFunctionEvaluationHandle(snapshot.Receiver.DetachHeapHandle());
        }
    }

    private ManagedResultsViewReceiverIdentity CaptureResultsViewReceiver(
        ManagedValueHandle handle)
    {
        ManagedValueOrigin? origin = NormalizeResultsViewOrigin(
            GetValueOrigin(handle), out nint heapValue);
        return new ManagedResultsViewReceiverIdentity(
            origin, heapValue == 0 ? 0 : CreateFunctionEvaluationHandle(heapValue),
            ownedHandle => ReleaseFunctionEvaluationHandle(ownedHandle));
    }

    private bool MatchesResultsViewReceiver(
        nint value, ManagedValueOrigin? origin, ManagedResultsViewReceiverIdentity receiver)
    {
        // Reference receivers have their own identity, independent of the slot holding them.
        nint heapValue = 0;
        nint dereferenced = 0;
        nint heap = 0;
        try
        {
            if (TryDereferenceValue(value, out dereferenced) &&
                ComAbi.TryQueryInterface(dereferenced, ICorDebugHeapValue2Abi.InterfaceId, out heap))
            {
                origin = new ManagedHeapValueOrigin(0);
                heapValue = dereferenced;
            }
            else
            {
                origin = NormalizeResultsViewOrigin(origin, out heapValue);
            }

            return origin is not null && origin == receiver.Origin &&
                (heapValue == 0
                    ? receiver.HeapHandle == 0
                    : receiver.HeapHandle != 0 &&
                        GetResultsViewHeapAddress(heapValue) ==
                        GetResultsViewHeapAddress(receiver.HeapHandle));
        }
        finally
        {
            ReleaseFunctionEvaluationPointer(heap);
            ReleaseFunctionEvaluationPointer(dereferenced);
        }
    }

    private ManagedValueOrigin? NormalizeResultsViewOrigin(
        ManagedValueOrigin? origin, out nint heapValue)
    {
        heapValue = 0;
        switch (origin)
        {
            case ManagedHeapValueOrigin heap:
                if (!_values.TryGetValue(heap.ValueReference, out ManagedValueHandle? owner))
                {
                    throw new InvalidOperationException("The enumerable's heap owner is stale.");
                }

                ValidateValueLifetime(owner);
                heapValue = owner.Pointer;
                return new ManagedHeapValueOrigin(0);
            case ManagedFieldValueOrigin field:
                return field with
                {
                    Parent = NormalizeResultsViewOrigin(field.Parent, out heapValue) ??
                        throw new InvalidOperationException("The enumerable field has no storage owner.")
                };
            case ManagedArrayElementValueOrigin element:
                return element with
                {
                    Parent = NormalizeResultsViewOrigin(element.Parent, out heapValue) ??
                        throw new InvalidOperationException("The enumerable element has no storage owner.")
                };
            default:
                return origin;
        }
    }

    private static unsafe ulong GetResultsViewHeapAddress(nint value)
    {
        nint dereferenced = DereferenceValue(value);
        try
        {
            ulong address = 0;
            ulong* addressPointer = &address;
            CorDebugHResult.ThrowIfFailed(
                new ICorDebugValueAbi(dereferenced).GetAddress((nint)addressPointer),
                "ICorDebugValue.GetAddress");
            return Volatile.Read(ref *addressPointer);
        }
        finally
        {
            _ = ComAbi.Release(dereferenced);
        }
    }
}
