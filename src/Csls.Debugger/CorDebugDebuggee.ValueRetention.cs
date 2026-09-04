using Csls.Debugger.Contracts;
using Csls.Debugger.Interop;

namespace Csls.Debugger;

/// <summary>
/// Retains expandable managed values and their optional memory handles.
/// </summary>
internal sealed partial class CorDebugDebuggee
{
    private ManagedValueReferences RetainValue(
        nint value,
        DebugStopGeneration generation,
        string? evaluateName,
        int? frameId,
        ManagedValueView view = ManagedValueView.Default,
        ManagedTupleCustomTypeInfo? tupleCustomTypeInfo = null)
    {
        if (!IsExpandable(value))
        {
            return default;
        }

        ManagedValueHandle handle = RetainRuntimeValue(
            value,
            generation,
            evaluateName,
            frameId,
            GetValueThreadId(frameId),
            view,
            tupleCustomTypeInfo);
        return new ManagedValueReferences(handle.Id, handle.MemoryReference);
    }

    private (int RuntimeValueReference, ManagedValueReferences References)
        RetainFunctionEvaluationValue(
        nint value,
        DebugStopGeneration generation,
        int threadId,
        ManagedValueView view = ManagedValueView.Default)
    {
        bool expandable = IsExpandable(value);
        ManagedValueHandle handle = RetainRuntimeValue(
            value,
            generation,
            evaluateName: null,
            frameId: null,
            threadId,
            view,
            tupleCustomTypeInfo: null);
        return (
            handle.Id,
            expandable
                ? new ManagedValueReferences(handle.Id, handle.MemoryReference)
                : default);
    }

    private ManagedValueHandle RetainRuntimeValue(
        nint value,
        DebugStopGeneration generation,
        string? evaluateName,
        int? frameId,
        int? threadId,
        ManagedValueView view,
        ManagedTupleCustomTypeInfo? tupleCustomTypeInfo)
    {
        nint identity;
        try
        {
            identity = ComAbi.GetIdentity(value);
        }
        catch (InvalidOperationException exception)
        {
            throw new InvalidOperationException(
                $"The runtime value '{evaluateName ?? "<anonymous>"}' did not expose its " +
                $"required COM identity: {exception.Message}",
                exception);
        }
        (nint Identity, int? FrameId, string? EvaluateName, ManagedValueView View) key = (
            identity,
            frameId,
            evaluateName,
            view);
        if (_valueIdentities.TryGetValue(key, out ManagedValueHandle? existing))
        {
            _ = ComAbi.Release(identity);
            return existing;
        }

        if (_values.Count >= MaximumExpandableValueCount)
        {
            _ = ComAbi.Release(identity);
            throw new InvalidOperationException(
                $"The stop exceeds the retained-value limit of {MaximumExpandableValueCount}.");
        }

        _ = ComAbi.AddRef(value);
        ulong memoryAddress = TryGetArrayAddress(value);
        int id = checked(++_nextVariablesReference);
        string? memoryReference = memoryAddress == 0
            ? null
            : $"csls-memory-{Guid.NewGuid():N}";
        var handle = new ManagedValueHandle
        {
            Id = id,
            Generation = generation,
            FrameId = frameId,
            ThreadId = threadId,
            Pointer = value,
            Identity = identity,
            View = view,
            MemoryReference = memoryReference,
            MemoryAddress = memoryAddress,
            EvaluateName = evaluateName,
            TupleCustomTypeInfo = tupleCustomTypeInfo
        };
        _values.Add(handle.Id, handle);
        _valueIdentities.Add(key, handle);
        if (memoryReference is not null)
        {
            _memoryValues.Add(memoryReference, handle);
        }

        return handle;
    }

    private int? GetValueThreadId(int? frameId) => frameId is int id
        ? _frames.Values.FirstOrDefault(frame => frame.Id == id)?.ThreadId
        : null;

    private static unsafe ulong TryGetArrayAddress(nint value)
    {
        nint dereferenced = 0;
        nint array = 0;
        try
        {
            if (!TryDereferenceValue(value, out dereferenced) ||
                !ComAbi.TryQueryInterface(
                    dereferenced,
                    ICorDebugArrayValueAbi.InterfaceId,
                    out array))
            {
                return 0;
            }

            ulong address = 0;
            ulong* addressPointer = &address;
            int result = new ICorDebugValueAbi(dereferenced).GetAddress((nint)addressPointer);
            return result >= 0 ? Volatile.Read(ref *addressPointer) : 0;
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

    private static bool IsExpandable(nint value)
    {
        nint dereferenced = 0;
        try
        {
            if (!TryDereferenceAndUnboxValue(value, out dereferenced))
            {
                return false;
            }

            if (ComAbi.TryQueryInterface(
                dereferenced,
                ICorDebugArrayValueAbi.InterfaceId,
                out nint array))
            {
                _ = ComAbi.Release(array);
                return true;
            }

            if (ComAbi.TryQueryInterface(
                dereferenced,
                ICorDebugObjectValueAbi.InterfaceId,
                out nint instance))
            {
                _ = ComAbi.Release(instance);
                return true;
            }

            return false;
        }
        finally
        {
            if (dereferenced != 0)
            {
                _ = ComAbi.Release(dereferenced);
            }
        }
    }
}
