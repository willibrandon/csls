namespace Csls.Mcp.Worker;

/// <summary>
/// Confirms one complete managed-exception policy replacement.
/// </summary>
/// <param name="DebugSession">The exact debugger-session identifier.</param>
/// <param name="StopGeneration">The generation in which the policy was replaced.</param>
/// <param name="Breakpoints">The normalized policy entries.</param>
internal sealed record McpDebugExceptionBreakpointsResult(
    string DebugSession,
    long StopGeneration,
    IReadOnlyList<McpDebugExceptionBreakpoint> Breakpoints) : IMcpDebugSessionResult;
