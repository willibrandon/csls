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
                    return ExpandArray(
                        array,
                        handle.EvaluateName,
                        handle.FrameId,
                        generation,
                        start,
                        count);
                }
                finally
                {
                    _ = ComAbi.Release(array);
                }
            }

            return ExpandObject(
                value,
                handle.EvaluateName,
                handle.FrameId,
                generation,
                start,
                count);
        }
        finally
        {
            _ = ComAbi.Release(value);
        }
    }

    private static unsafe nint DereferenceValue(nint value)
    {
        return TryDereferenceValue(value, out nint result)
            ? result
            : throw new InvalidOperationException("A null managed value cannot be expanded.");
    }

    private static unsafe bool TryDereferenceValue(nint value, out nint result)
    {
        if (!ComAbi.TryQueryInterface(
            value,
            ICorDebugReferenceValueAbi.InterfaceId,
            out nint reference))
        {
            result = Retain(value);
            return true;
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
                result = 0;
                return false;
            }

            nint dereferenced = 0;
            nint* resultAddress = &dereferenced;
            CorDebugHResult.ThrowIfFailed(
                new ICorDebugReferenceValueAbi(reference).Dereference((nint)resultAddress),
                "ICorDebugReferenceValue.Dereference");
            result = Volatile.Read(ref *resultAddress);
            return result != 0
                ? true
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
