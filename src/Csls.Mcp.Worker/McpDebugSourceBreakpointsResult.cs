using Csls.Debugger.Contracts;

namespace Csls.Mcp.Worker;

/// <summary>
/// Returns one complete source-breakpoint replacement result.
/// </summary>
/// <param name="DebugSession">The exact debugger-session identifier.</param>
/// <param name="StopGeneration">The generation in which the set was replaced.</param>
/// <param name="Breakpoints">The ordered binding results.</param>
internal sealed record McpDebugSourceBreakpointsResult(
    string DebugSession,
    long StopGeneration,
    IReadOnlyList<DebugSourceBreakpointInfo> Breakpoints);
