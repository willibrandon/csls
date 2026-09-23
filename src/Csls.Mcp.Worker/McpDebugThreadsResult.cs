using Csls.Debugger.Contracts;

namespace Csls.Mcp.Worker;

/// <summary>
/// Carries current-generation managed threads for one debugger session.
/// </summary>
/// <param name="DebugSession">The explicit debugger-session identifier.</param>
/// <param name="StopGeneration">The stop generation that owns the result.</param>
/// <param name="Threads">The bounded managed thread list.</param>
internal sealed record McpDebugThreadsResult(
    string DebugSession,
    long StopGeneration,
    IReadOnlyList<DebugThreadInfo> Threads) : IMcpDebugSessionResult;
