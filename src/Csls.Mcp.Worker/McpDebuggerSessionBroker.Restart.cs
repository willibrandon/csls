using Csls.Debugger.Contracts;

namespace Csls.Mcp.Worker;

/// <summary>
/// Restarts explicitly authorized debugger targets.
/// </summary>
internal sealed partial class McpDebuggerSessionBroker
{
    /// <summary>
    /// Restarts a stopped target while retaining its debugger-session identity.
    /// </summary>
    /// <param name="debugSession">The exact debugger-session identifier.</param>
    /// <param name="stopGeneration">The exact current stopped generation.</param>
    /// <param name="cancellationToken">The MCP request cancellation token.</param>
    /// <returns>The replacement target state.</returns>
    internal async Task<McpDebugSessionInfo> RestartAsync(
        string debugSession,
        long stopGeneration,
        CancellationToken cancellationToken)
    {
        McpDebuggerSession session = Resolve(debugSession);
        RequireAgentControl(session);
        DebugSessionSnapshot restarted = await session.InvokeAsync(
            async (client, token) =>
            {
                DebugSessionSnapshot current = await client.GetSessionAsync(token)
                    .ConfigureAwait(false);
                RequireStoppedGeneration(current, stopGeneration);
                return await client.RestartAsync(token).ConfigureAwait(false);
            },
            cancellationToken).ConfigureAwait(false);
        return McpDebugSessionInfo.Create(
            session.Id,
            session.Kind,
            session.AgentControl,
            restarted);
    }
}
