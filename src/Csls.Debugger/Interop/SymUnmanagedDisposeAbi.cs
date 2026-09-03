namespace Csls.Debugger.Interop;

/// <summary>
/// Projects the native ISymUnmanagedDispose lifetime boundary.
/// </summary>
internal readonly unsafe struct SymUnmanagedDisposeAbi
{
    private readonly nint _instance;

    /// <summary>
    /// Creates a projection over a non-null ISymUnmanagedDispose pointer.
    /// </summary>
    /// <param name="instance">The native COM interface pointer.</param>
    internal SymUnmanagedDisposeAbi(nint instance)
    {
        ArgumentOutOfRangeException.ThrowIfZero(instance);
        _instance = instance;
    }

    /// <summary>
    /// Gets the ISymUnmanagedDispose interface identifier.
    /// </summary>
    internal static Guid InterfaceId => new("969708D2-05E5-4861-A3B0-96E473CDF63F");

    /// <summary>
    /// Destroys reader-owned native resources before the final COM release.
    /// </summary>
    /// <returns>The HRESULT returned by the symbol reader.</returns>
    internal int Destroy()
    {
        nint* vtable = *(nint**)_instance;
        var operation = (delegate* unmanaged[Stdcall]<nint, int>)vtable[3];
        return operation(_instance);
    }
}
