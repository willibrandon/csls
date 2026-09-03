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
        DebugStopGeneration generation)
    {
        if (!IsExpandable(value))
        {
            return default;
        }

        nint identity = ComAbi.GetIdentity(value);
        if (_valueIdentities.TryGetValue(identity, out ManagedValueHandle? existing))
        {
            _ = ComAbi.Release(identity);
            return new ManagedValueReferences(existing.Id, existing.MemoryReference);
        }

        if (_values.Count >= MaximumExpandableValueCount)
        {
            _ = ComAbi.Release(identity);
            throw new InvalidOperationException(
                $"The stop exceeds the expandable-value limit of {MaximumExpandableValueCount}.");
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
            Pointer = value,
            Identity = identity,
            MemoryReference = memoryReference,
            MemoryAddress = memoryAddress
        };
        _values.Add(handle.Id, handle);
        _valueIdentities.Add(handle.Identity, handle);
        if (memoryReference is not null)
        {
            _memoryValues.Add(memoryReference, handle);
        }

        return new ManagedValueReferences(handle.Id, memoryReference);
    }

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
            if (!TryDereferenceValue(value, out dereferenced))
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
