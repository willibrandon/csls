namespace Csls.Debugger.Interop;

/// <summary>
/// Provides deterministic ownership operations for native COM interface pointers.
/// </summary>
internal static unsafe class ComAbi
{
    private static readonly Guid s_iUnknownInterfaceId =
        new("00000000-0000-0000-C000-000000000046");

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
    /// Tries to query a native COM object for an optional interface.
    /// </summary>
    /// <param name="instance">The source COM interface pointer.</param>
    /// <param name="interfaceId">The requested interface identifier.</param>
    /// <param name="result">Receives the owned interface pointer when supported.</param>
    /// <returns>True when the object supports the requested interface.</returns>
    internal static bool TryQueryInterface(
        nint instance,
        in Guid interfaceId,
        out nint result)
    {
        const int noInterfaceHResult = unchecked((int)0x80004002);
        ArgumentOutOfRangeException.ThrowIfZero(instance);
        nint* vtable = *(nint**)instance;
        var queryInterface =
            (delegate* unmanaged[Stdcall]<nint, Guid*, nint*, int>)vtable[0];
        nint queriedInterface = 0;
        Guid localInterfaceId = interfaceId;
        int hresult = queryInterface(instance, &localInterfaceId, &queriedInterface);
        queriedInterface = Volatile.Read(ref queriedInterface);
        if (hresult == noInterfaceHResult)
        {
            result = 0;
            return false;
        }

        CorDebugHResult.ThrowIfFailed(hresult, "IUnknown.QueryInterface");
        result = queriedInterface;
        return queriedInterface != 0;
    }

    /// <summary>
    /// Gets an owned canonical COM identity pointer for an interface.
    /// </summary>
    /// <param name="instance">The source COM interface pointer.</param>
    /// <returns>The owned IUnknown identity pointer.</returns>
    internal static nint GetIdentity(nint instance) =>
        QueryInterface(instance, s_iUnknownInterfaceId);

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
