using Csls.Debugger.Contracts;

namespace Csls.Mcp.Worker;

/// <summary>
/// Returns runtime-approved instruction-pointer destinations for one stop.
/// </summary>
/// <param name="DebugSession">The exact debugger-session identifier.</param>
/// <param name="StopGeneration">The inspected stop generation.</param>
/// <param name="Targets">The runtime-approved destinations.</param>
internal sealed record McpDebugGotoTargetsResult(
    string DebugSession,
    long StopGeneration,
    IReadOnlyList<DebugGotoTargetInfo> Targets);
