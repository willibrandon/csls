using Csls.Debugger.Contracts;

namespace Csls.Mcp.Worker;

/// <summary>
/// Reads authoritative breakpoint state for MCP agents.
/// </summary>
internal sealed partial class McpDebuggerSessionBroker
{
    /// <summary>
    /// Gets every configured breakpoint without granting target control.
    /// </summary>
    internal Task<McpDebugBreakpointsResult> GetBreakpointsAsync(
        string debugSession,
        CancellationToken cancellationToken)
    {
        McpDebuggerSession session = Resolve(debugSession);
        return session.InvokeAsync(
            async (client, token) =>
            {
                DebugSessionSnapshot current = await client.GetSessionAsync(token)
                    .ConfigureAwait(false);
                DebugBreakpointSnapshot breakpoints = await client.GetBreakpointsAsync(token)
                    .ConfigureAwait(false);
                string state = McpDebugSessionInfo.Create(
                    session.Id,
                    session.Kind,
                    session.AgentControl,
                    current).State;
                return new McpDebugBreakpointsResult(
                    session.Id,
                    state,
                    current.StopGeneration,
                    breakpoints.SourceBreakpoints,
                    breakpoints.FunctionBreakpoints,
                    breakpoints.InstructionBreakpoints,
                    breakpoints.ExceptionBreakpoints
                        .Select(McpDebugExceptionBreakpoint.Create)
                        .ToArray());
            },
            cancellationToken);
    }
}
