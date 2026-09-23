using Csls.Debugger.Contracts;

namespace Csls.Mcp.Worker;

/// <summary>
/// Returns source-aware Step Into targets for one exact stop generation.
/// </summary>
/// <param name="DebugSession">The exact debugger-session identifier.</param>
/// <param name="StopGeneration">The inspected stop generation.</param>
/// <param name="Targets">The selectable managed calls.</param>
internal sealed record McpDebugStepTargetsResult(
    string DebugSession,
    long StopGeneration,
    IReadOnlyList<DebugStepTargetInfo> Targets) : IMcpDebugSessionResult;
