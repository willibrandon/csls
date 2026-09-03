namespace Csls.Mcp.Worker;

/// <summary>
/// Returns one bounded generation-aware target-memory read.
/// </summary>
/// <param name="DebugSession">The exact debugger-session identifier.</param>
/// <param name="StopGeneration">The inspected stop generation.</param>
/// <param name="Address">The first returned or unreadable target address.</param>
/// <param name="Data">The bytes encoded as base64 by JSON.</param>
/// <param name="UnreadableBytes">The unreadable trailing byte count.</param>
internal sealed record McpDebugMemoryResult(
    string DebugSession,
    long StopGeneration,
    ulong Address,
    ReadOnlyMemory<byte> Data,
    int UnreadableBytes);
