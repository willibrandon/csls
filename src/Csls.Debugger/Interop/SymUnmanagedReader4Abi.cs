namespace Csls.Debugger.Interop;

/// <summary>
/// Projects the native ISymUnmanagedReader4 methods needed for Portable PDB recovery.
/// </summary>
internal readonly unsafe struct SymUnmanagedReader4Abi
{
    private const int GetPortableDebugMetadataSlot = 26;
    private readonly nint _instance;

    /// <summary>
    /// Creates a projection over a non-null ISymUnmanagedReader4 pointer.
    /// </summary>
    /// <param name="instance">The native COM interface pointer.</param>
    internal SymUnmanagedReader4Abi(nint instance)
    {
        ArgumentOutOfRangeException.ThrowIfZero(instance);
        _instance = instance;
    }

    /// <summary>
    /// Gets the ISymUnmanagedReader4 interface identifier.
    /// </summary>
    internal static Guid InterfaceId => new("E65C58B7-2948-434D-8A6D-481740A00C16");

    /// <summary>
    /// Gets the borrowed Portable PDB metadata pointer and size from the reader.
    /// </summary>
    /// <param name="metadata">Receives the reader-owned metadata pointer.</param>
    /// <param name="size">Receives the metadata size in bytes.</param>
    /// <returns>The HRESULT returned by the symbol reader.</returns>
    internal int GetPortableDebugMetadata(nint metadata, nint size)
    {
        nint* vtable = *(nint**)_instance;
        var operation =
            (delegate* unmanaged[Stdcall]<nint, nint, nint, int>)vtable[
                GetPortableDebugMetadataSlot];
        return operation(_instance, metadata, size);
    }
}
