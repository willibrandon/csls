using Csls.Debugger.Interop;

namespace Csls.Debugger;

/// <summary>
/// Owns one exact runtime type and borrowed immutable storage for a captured value.
/// </summary>
/// <param name="type">The runtime type reference transferred to this owner.</param>
/// <param name="address">The captured storage address, or zero for a null reference.</param>
/// <param name="size">The number of bytes remaining in the containing storage.</param>
/// <param name="heapObject">Whether the address identifies a managed heap object.</param>
/// <param name="typeId">The opaque identity read directly from the captured heap object, when available.</param>
internal sealed class CorDebugDumpStorage(nint type, ulong address, ulong size, bool heapObject, CorDebugTypeId? typeId = null) : IDisposable
{
    private nint _type = type;

    /// <summary>
    /// Gets the borrowed exact type for the lifetime of this value.
    /// </summary>
    internal nint Type => _type;

    /// <summary>
    /// Gets the captured storage address.
    /// </summary>
    internal ulong Address { get; } = address;

    /// <summary>
    /// Gets the remaining containing storage extent.
    /// </summary>
    internal ulong Size { get; } = size;

    /// <summary>
    /// Gets whether this value starts at a heap object header.
    /// </summary>
    internal bool HeapObject { get; } = heapObject;

    /// <summary>
    /// Gets the opaque type identity supplied by actual heap storage.
    /// </summary>
    internal CorDebugTypeId? TypeId { get; } = typeId;

    /// <inheritdoc />
    public void Dispose()
    {
        nint owned = Interlocked.Exchange(ref _type, 0);
        if (owned != 0)
        {
            _ = ComAbi.Release(owned);
        }
    }
}
