using Csls.Debugger.Interop;

namespace Csls.Debugger;

/// <summary>
/// Identifies retained heap receivers and exact physical value storage for inspection.
/// </summary>
internal sealed partial class CorDebugDebuggee
{
    private ManagedValueOrigin? GetValueOrigin(ManagedValueHandle handle)
    {
        ValidateValueLifetime(handle);
        nint dereferenced = 0;
        nint heapValue = 0;
        try
        {
            if (TryDereferenceValue(handle.Pointer, out dereferenced) &&
                ComAbi.TryQueryInterface(
                    dereferenced, ICorDebugHeapValue2Abi.InterfaceId, out heapValue))
            {
                return RetainHeapValueOrigin(dereferenced, handle);
            }

            return handle.Origin;
        }
        finally
        {
            ReleaseFunctionEvaluationPointer(heapValue);
            ReleaseFunctionEvaluationPointer(dereferenced);
        }
    }

    private unsafe ManagedHeapValueOrigin RetainHeapValueOrigin(
        nint heapValue,
        ManagedValueHandle source)
    {
        ulong address = 0;
        ulong* addressPointer = &address;
        CorDebugHResult.ThrowIfFailed(
            new ICorDebugValueAbi(heapValue).GetAddress((nint)addressPointer),
            "ICorDebugValue.GetAddress");
        address = Volatile.Read(ref *addressPointer);
        if (address == 0)
        {
            throw new InvalidOperationException("The managed heap object has no runtime address.");
        }

        (ulong Address, ManagedResultsViewLifetime? Lifetime) key = (address, source.Lifetime);
        if (_heapValueOrigins.TryGetValue(key, out ManagedHeapValueOrigin? origin))
        {
            return origin;
        }

        // Retain the original heap object, including its box, rather than the
        // mutable reference slot used to reach it. Execution clears this cache.
        ManagedValueHandle retained = RetainRuntimeValue(
            heapValue,
            source.Generation,
            evaluateName: null,
            frameId: null,
            source.ThreadId,
            ManagedValueView.Default,
            tupleCustomTypeInfo: null,
            origin: null,
            lifetime: source.Lifetime);
        origin = new ManagedHeapValueOrigin(retained.Id);
        _heapValueOrigins.Add(key, origin);
        return origin;
    }

    private ManagedValueOrigin? GetValueOrigin(ManagedExpressionValue value)
    {
        if (value.RuntimeValueReference <= 0)
        {
            return null;
        }

        if (!_values.TryGetValue(value.RuntimeValueReference, out ManagedValueHandle? handle))
        {
            throw new InvalidOperationException(
                $"Variable reference {value.RuntimeValueReference} is stale or unknown.");
        }

        return GetValueOrigin(handle);
    }

    private ManagedFieldValueOrigin? CreateFieldValueOrigin(
        ManagedValueOrigin? parent,
        nint declaringClass,
        uint fieldToken)
    {
        if (parent is null)
        {
            return null;
        }

        nint module = GetClassModule(declaringClass);
        try
        {
            CorDebugLoadedModule? loadedModule = _sourceBreakpoints.FindModule(module);
            return loadedModule is null
                ? null
                : new ManagedFieldValueOrigin(
                    parent, loadedModule.Id, GetClassToken(declaringClass), fieldToken);
        }
        finally
        {
            ReleaseFunctionEvaluationPointer(module);
        }
    }
}
