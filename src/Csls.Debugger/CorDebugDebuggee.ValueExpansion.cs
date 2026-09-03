using Csls.Debugger.Contracts;
using Csls.Debugger.Interop;

namespace Csls.Debugger;

/// <summary>
/// Retains and dispatches generation-bound expandable managed values.
/// </summary>
internal sealed partial class CorDebugDebuggee
{
    private const int MaximumExpandableValueCount = 64 * 1024;

    private List<DebugVariableInfo> ExpandValue(
        int variablesReference,
        DebugStopGeneration generation,
        int start,
        int count)
    {
        if (!_values.TryGetValue(variablesReference, out ManagedValueHandle? handle))
        {
            throw new InvalidOperationException(
                $"Variable reference {variablesReference} is stale or unknown.");
        }

        ValidateGeneration(variablesReference, handle.Generation, generation);
        nint value = DereferenceValue(handle.Pointer);
        try
        {
            if (ComAbi.TryQueryInterface(
                value,
                ICorDebugArrayValueAbi.InterfaceId,
                out nint array))
            {
                try
                {
                    return ExpandArray(array, generation, start, count);
                }
                finally
                {
                    _ = ComAbi.Release(array);
                }
            }

            return ExpandObject(value, generation, start, count);
        }
        finally
        {
            _ = ComAbi.Release(value);
        }
    }

    private int RetainExpandableValue(nint value, DebugStopGeneration generation)
    {
        if (!IsExpandable(value))
        {
            return 0;
        }

        nint identity = ComAbi.GetIdentity(value);
        if (_valueIdentities.TryGetValue(identity, out ManagedValueHandle? existing))
        {
            _ = ComAbi.Release(identity);
            return existing.Id;
        }

        if (_values.Count >= MaximumExpandableValueCount)
        {
            _ = ComAbi.Release(identity);
            throw new InvalidOperationException(
                $"The stop exceeds the expandable-value limit of {MaximumExpandableValueCount}.");
        }

        _ = ComAbi.AddRef(value);
        var handle = new ManagedValueHandle
        {
            Id = checked(++_nextVariablesReference),
            Generation = generation,
            Pointer = value,
            Identity = identity
        };
        _values.Add(handle.Id, handle);
        _valueIdentities.Add(handle.Identity, handle);
        return handle.Id;
    }

    private static bool IsExpandable(nint value)
    {
        nint dereferenced = 0;
        try
        {
            dereferenced = DereferenceValue(value);
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

    private static unsafe nint DereferenceValue(nint value)
    {
        if (!ComAbi.TryQueryInterface(
            value,
            ICorDebugReferenceValueAbi.InterfaceId,
            out nint reference))
        {
            return Retain(value);
        }

        try
        {
            int isNull = 0;
            int* isNullAddress = &isNull;
            CorDebugHResult.ThrowIfFailed(
                new ICorDebugReferenceValueAbi(reference).IsNull((nint)isNullAddress),
                "ICorDebugReferenceValue.IsNull");
            if (Volatile.Read(ref *isNullAddress) != 0)
            {
                throw new InvalidOperationException("A null managed value cannot be expanded.");
            }

            nint result = 0;
            nint* resultAddress = &result;
            CorDebugHResult.ThrowIfFailed(
                new ICorDebugReferenceValueAbi(reference).Dereference((nint)resultAddress),
                "ICorDebugReferenceValue.Dereference");
            result = Volatile.Read(ref *resultAddress);
            return result != 0
                ? result
                : throw new InvalidOperationException(
                    "ICorDebugReferenceValue.Dereference returned no value.");
        }
        finally
        {
            _ = ComAbi.Release(reference);
        }
    }

    private static nint Retain(nint value)
    {
        _ = ComAbi.AddRef(value);
        return value;
    }
}
