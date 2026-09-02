namespace Csls.Debugger.Interop;

/// <summary>
/// Provides deterministic ownership operations for native COM interface pointers.
/// </summary>
internal static unsafe class ComAbi
{
    /// <summary>
    /// Queries a native COM object for one explicitly selected interface.
    /// </summary>
    /// <param name="instance">The source COM interface pointer.</param>
    /// <param name="interfaceId">The requested interface identifier.</param>
    /// <returns>The owned interface pointer returned by QueryInterface.</returns>
    internal static nint QueryInterface(nint instance, in Guid interfaceId)
    {
        ArgumentOutOfRangeException.ThrowIfZero(instance);
        nint* vtable = *(nint**)instance;
        var queryInterface =
            (delegate* unmanaged[Stdcall]<nint, Guid*, nint*, int>)vtable[0];
        nint result = 0;
        Guid localInterfaceId = interfaceId;
        int hresult = queryInterface(instance, &localInterfaceId, &result);
        CorDebugHResult.ThrowIfFailed(hresult, "IUnknown.QueryInterface");
        return result;
    }

    /// <summary>
    /// Adds one ownership reference to a native COM interface pointer.
    /// </summary>
    /// <param name="instance">The COM interface pointer to retain.</param>
    /// <returns>The implementation-defined reference count.</returns>
    internal static uint AddRef(nint instance)
    {
        ArgumentOutOfRangeException.ThrowIfZero(instance);
        nint* vtable = *(nint**)instance;
        var addRef = (delegate* unmanaged[Stdcall]<nint, uint>)vtable[1];
        return addRef(instance);
    }

    /// <summary>
    /// Releases one ownership reference from a native COM interface pointer.
    /// </summary>
    /// <param name="instance">The COM interface pointer to release.</param>
    /// <returns>The implementation-defined remaining reference count.</returns>
    internal static uint Release(nint instance)
    {
        ArgumentOutOfRangeException.ThrowIfZero(instance);
        nint* vtable = *(nint**)instance;
        var release = (delegate* unmanaged[Stdcall]<nint, uint>)vtable[2];
        return release(instance);
    }
}
