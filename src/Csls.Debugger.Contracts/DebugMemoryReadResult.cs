namespace Csls.Debugger.Contracts;

/// <summary>
/// Contains one bounded target-memory read and its first resolved address.
/// </summary>
/// <param name="Address">The address of the first returned or unreadable byte.</param>
/// <param name="Data">The contiguous bytes read before any unreadable range.</param>
/// <param name="UnreadableBytes">The unreadable trailing byte count.</param>
public sealed record DebugMemoryReadResult(
    ulong Address,
    ReadOnlyMemory<byte> Data,
    int UnreadableBytes);
