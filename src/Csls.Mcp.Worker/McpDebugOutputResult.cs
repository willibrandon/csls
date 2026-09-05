namespace Csls.Mcp.Worker;

/// <summary>
/// Returns one bounded cursor-addressable target-output page.
/// </summary>
/// <param name="DebugSession">The exact debugger-session identifier.</param>
/// <param name="State">The lifecycle state observed with the page.</param>
/// <param name="StopGeneration">The latest stop generation observed with the page.</param>
/// <param name="Entries">The ordered retained output entries.</param>
/// <param name="NextSequence">The cursor for the next output request.</param>
/// <param name="FirstRetainedSequence">The oldest sequence still retained.</param>
/// <param name="DroppedBeforeStart">Entries lost between the cursor and retained window.</param>
/// <param name="HasMore">Whether another retained entry follows this page.</param>
internal sealed record McpDebugOutputResult(
    string DebugSession,
    string State,
    long StopGeneration,
    IReadOnlyList<McpDebugOutputEntry> Entries,
    long NextSequence,
    long FirstRetainedSequence,
    long DroppedBeforeStart,
    bool HasMore) : IMcpDebugSessionResult;
