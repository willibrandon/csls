using Csls.Debugger.Interop;

namespace Csls.Debugger;

/// <summary>
/// Identifies retained heap receivers and exact physical value storage for inspection.
/// </summary>
internal sealed partial class CorDebugDebuggee
{
    private static ManagedValueOrigin? GetValueOrigin(ManagedValueHandle handle)
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
                return new ManagedHeapValueOrigin(handle.Id);
            }

            return handle.Origin;
        }
        finally
        {
            ReleaseFunctionEvaluationPointer(heapValue);
            ReleaseFunctionEvaluationPointer(dereferenced);
        }
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
