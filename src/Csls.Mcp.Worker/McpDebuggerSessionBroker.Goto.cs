using Csls.Debugger.Contracts;

namespace Csls.Mcp.Worker;

/// <summary>
/// Applies runtime-approved instruction-pointer movement for MCP agents.
/// </summary>
internal sealed partial class McpDebuggerSessionBroker
{
    /// <summary>
    /// Moves one thread to a generation-bound runtime-approved destination.
    /// </summary>
    internal Task<McpDebugSessionInfo> GotoAsync(
        string debugSession,
        long stopGeneration,
        int threadId,
        int targetId,
        CancellationToken cancellationToken)
    {
        ValidatePositive(threadId, nameof(threadId));
        ValidatePositive(targetId, nameof(targetId));
        McpDebuggerSession session = Resolve(debugSession);
        return InvokeControlledStoppedAsync(
            session,
            stopGeneration,
            async (selected, client, token) => selected.CreateInfo(
                await client.GotoAsync(
                    new DebugGotoRequest(threadId, targetId),
                    token).ConfigureAwait(false)),
            cancellationToken);
    }
}
