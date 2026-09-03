using Csls.Debugger.Contracts;

namespace Csls.Mcp.Worker;

/// <summary>
/// Manages revocable time-bounded debugger control grants for one MCP connection.
/// </summary>
internal sealed partial class McpDebuggerSessionBroker
{
    private const int MaximumAgentControlDurationSeconds = 3600;

    /// <summary>
    /// Grants or revokes target-changing authority for one explicit session.
    /// </summary>
    /// <param name="debugSession">The exact debugger-session identifier.</param>
    /// <param name="enabled">Whether target-changing authority should be active.</param>
    /// <param name="durationSeconds">The required grant duration when enabling control.</param>
    /// <param name="cancellationToken">The MCP request cancellation token.</param>
    /// <returns>The session state with its resulting authorization projection.</returns>
    internal Task<McpDebugSessionInfo> SetAgentControlAsync(
        string debugSession,
        bool enabled,
        int? durationSeconds,
        CancellationToken cancellationToken)
    {
        if (enabled && durationSeconds is not (> 0 and <= MaximumAgentControlDurationSeconds))
        {
            throw InvalidRequest(
                $"durationSeconds must be between 1 and " +
                $"{MaximumAgentControlDurationSeconds} when enabling agent control.");
        }

        if (!enabled && durationSeconds is not null)
        {
            throw InvalidRequest(
                "durationSeconds must be omitted when revoking agent control.");
        }

        McpDebuggerSession session = Resolve(debugSession);
        return session.InvokeAsync(
            async (client, token) =>
            {
                if (!enabled)
                {
                    session.SetAgentControl(enabled: false, TimeSpan.Zero);
                }

                DebugSessionSnapshot snapshot = await client.GetSessionAsync(token)
                    .ConfigureAwait(false);
                if (enabled)
                {
                    session.SetAgentControl(
                        enabled: true,
                        TimeSpan.FromSeconds(durationSeconds!.Value));
                }

                return session.CreateInfo(snapshot);
            },
            cancellationToken);
    }
}
