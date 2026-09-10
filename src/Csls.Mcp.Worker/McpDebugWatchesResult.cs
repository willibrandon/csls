namespace Csls.Mcp.Worker;

/// <summary>
/// Carries independently evaluated watches for one current debugger stop.
/// </summary>
/// <param name="DebugSession">The explicit debugger-session identifier.</param>
/// <param name="StopGeneration">The stop generation that owns the watch values.</param>
/// <param name="Watches">The ordered per-expression results.</param>
internal sealed record McpDebugWatchesResult(
    string DebugSession,
    long StopGeneration,
    IReadOnlyList<McpDebugWatchValue> Watches) : IMcpDebugSessionResult;
