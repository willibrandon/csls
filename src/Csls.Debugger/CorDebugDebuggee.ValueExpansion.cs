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
        int count,
        DebugVariableFilter filter)
    {
        if (!_values.TryGetValue(variablesReference, out ManagedValueHandle? handle))
        {
            throw new InvalidOperationException(
                $"Variable reference {variablesReference} is stale or unknown.");
        }

        ValidateGeneration(variablesReference, handle.Generation, generation);
        ValidateValueLifetime(handle);
        if (handle.View == ManagedValueView.ResultsView)
        {
            throw new InvalidOperationException(
                "Expanding Results View executes target code and requires explicit " +
                "target-code authorization.");
        }

        List<DebugVariableInfo> result = ExpandRetainedValue(handle, generation, start, count, filter);
        foreach (ManagedValueHandle child in result.Select(variable =>
            _values.GetValueOrDefault(variable.VariablesReference)).OfType<ManagedValueHandle>())
        {
            child.ThreadId ??= handle.ThreadId;
        }

        return result;
    }

    private List<DebugVariableInfo> ExpandRetainedValue(
        ManagedValueHandle handle,
        DebugStopGeneration generation,
        int start,
        int count,
        DebugVariableFilter filter)
    {
        if (handle.SyntheticVariables is IReadOnlyList<DebugVariableInfo> syntheticVariables)
        {
            var state = new ManagedObjectExpansionState { Filter = filter };
            IEnumerable<DebugVariableInfo> variables = syntheticVariables
                .Where(variable => state.Includes(variable.IsIndexed))
                .Skip(start);
            return count == 0 ? [.. variables] : [.. variables.Take(count)];
        }

        nint value = TryDereferenceAndUnboxValue(handle.Pointer, out nint inspectedValue)
            ? inspectedValue
            : throw new InvalidOperationException("A null managed value cannot be expanded.");
        try
        {
            if (ComAbi.TryQueryInterface(
                value,
                ICorDebugArrayValueAbi.InterfaceId,
                out nint array))
            {
                try
                {
                    if (filter == DebugVariableFilter.Named)
                    {
                        return [];
                    }

                    return ExpandArray(
                        array,
                        handle.EvaluateName,
                        handle.FrameId,
                        generation,
                        handle.TupleCustomTypeInfo,
                        start,
                        count,
                        GetValueOrigin(handle),
                        handle.Lifetime);
                }
                finally
                {
                    _ = ComAbi.Release(array);
                }
            }

            ManagedDebuggerTypeProxyRawView? proxyRawView = null;
            ManagedDebuggerTypeProxyStaticView? proxyStaticView = null;
            IReadOnlyList<ManagedDebuggerTypeProxyPropertyPresentation>? proxyProperties = null;
            if (handle.ProxyRawValueReference > 0)
            {
                if (!_values.TryGetValue(
                    handle.ProxyRawValueReference,
                    out ManagedValueHandle? rawHandle))
                {
                    throw new InvalidOperationException(
                        "The debugger type proxy no longer owns its original value.");
                }

                ValidateGeneration(rawHandle.Id, rawHandle.Generation, generation);
                ValidateValueLifetime(rawHandle);
                proxyRawView = new ManagedDebuggerTypeProxyRawView(
                    rawHandle.Pointer,
                    rawHandle.Id,
                    rawHandle.MemoryReference);
                proxyProperties = handle.ProxyProperties;
            }

            if (handle.ProxyStaticValueReference > 0)
            {
                if (!_values.TryGetValue(
                    handle.ProxyStaticValueReference,
                    out ManagedValueHandle? staticHandle))
                {
                    throw new InvalidOperationException(
                        "The debugger type proxy no longer owns its static members.");
                }

                ValidateGeneration(staticHandle.Id, staticHandle.Generation, generation);
                ValidateValueLifetime(staticHandle);
                proxyStaticView = new ManagedDebuggerTypeProxyStaticView(staticHandle.Id);
            }

            return ExpandObject(
                value,
                handle.EvaluateName,
                handle.FrameId,
                generation,
                start,
                count,
                handle.View,
                handle.TupleCustomTypeInfo,
                proxyRawView,
                proxyStaticView,
                proxyProperties,
                handle.ThreadId,
                filter,
                GetValueOrigin(handle),
                handle.Lifetime);
        }
        finally
        {
            _ = ComAbi.Release(value);
        }
    }

    private static nint DereferenceValue(nint value)
    {
        return TryDereferenceValue(value, out nint result)
            ? result
            : throw new InvalidOperationException("A null managed value cannot be expanded.");
    }

    private static unsafe bool TryDereferenceAndUnboxValue(nint value, out nint result)
    {
        if (!TryDereferenceValue(value, out nint dereferenced))
        {
            result = 0;
            return false;
        }

        nint box = 0;
        try
        {
            if (!ComAbi.TryQueryInterface(
                dereferenced,
                ICorDebugBoxValueAbi.InterfaceId,
                out box))
            {
                result = dereferenced;
                dereferenced = 0;
                return true;
            }

            nint unboxed = 0;
            nint* unboxedAddress = &unboxed;
            CorDebugHResult.ThrowIfFailed(
                new ICorDebugBoxValueAbi(box).GetObject((nint)unboxedAddress),
                "ICorDebugBoxValue.GetObject");
            result = RequirePointer(
                Volatile.Read(ref *unboxedAddress),
                "ICorDebugBoxValue.GetObject");
            return true;
        }
        finally
        {
            if (box != 0)
            {
                _ = ComAbi.Release(box);
            }

            if (dereferenced != 0)
            {
                _ = ComAbi.Release(dereferenced);
            }
        }
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
            if (result == 0)
            {
                throw new InvalidOperationException(
                    "ICorDebugReferenceValue.Dereference returned no value.");
            }

            return true;
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
