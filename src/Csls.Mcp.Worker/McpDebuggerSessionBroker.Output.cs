using Csls.Debugger.Contracts;

namespace Csls.Mcp.Worker;

/// <summary>
/// Reads bounded cursor-addressable target output for MCP agents.
/// </summary>
internal sealed partial class McpDebuggerSessionBroker
{
    /// <summary>
    /// Gets one retained target-output page after a stable sequence cursor.
    /// </summary>
    internal Task<McpDebugOutputResult> GetOutputAsync(
        string debugSession,
        long afterSequence,
        int count,
        CancellationToken cancellationToken)
    {
        if (afterSequence < 0 || count is <= 0 or > MaximumPageSize)
        {
            throw InvalidRequest(
                $"afterSequence must be non-negative and count must be between 1 and " +
                $"{MaximumPageSize}.");
        }

        McpDebuggerSession session = Resolve(debugSession);
        return session.InvokeAsync(
            async (client, token) =>
            {
                DebugSessionSnapshot snapshot = await client.GetSessionAsync(token)
                    .ConfigureAwait(false);
                DebugOutputPage page = await client.GetOutputAsync(
                    new DebugOutputRequest(afterSequence, count),
                    token).ConfigureAwait(false);
                var info = McpDebugSessionInfo.Create(
                    session.Id,
                    session.Kind,
                    session.AgentControl,
                    snapshot);
                return new McpDebugOutputResult(
                    session.Id,
                    info.State,
                    snapshot.StopGeneration,
                    page.Entries.Select(McpDebugOutputEntry.Create).ToArray(),
                    page.NextSequence,
                    page.FirstRetainedSequence,
                    page.DroppedBeforeStart,
                    page.HasMore);
            },
            cancellationToken);
    }
}
