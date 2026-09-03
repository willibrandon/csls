using Csls.Debugger.Contracts;
using Csls.Debugger.Control;

namespace Csls.Mcp.Worker;

/// <summary>
/// Enforces debugger authorization, generation, and bounded-input invariants.
/// </summary>
internal sealed partial class McpDebuggerSessionBroker
{
    private Task<T> InvokeStoppedAsync<T>(
        string debugSession,
        long stopGeneration,
        Func<McpDebuggerSession, DebuggerRpcClient, CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken) =>
        InvokeStoppedAsync(
            Resolve(debugSession),
            stopGeneration,
            operation,
            cancellationToken);

    private static async Task<T> InvokeStoppedAsync<T>(
        McpDebuggerSession session,
        long stopGeneration,
        Func<McpDebuggerSession, DebuggerRpcClient, CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken)
    {
        if (stopGeneration <= 0)
        {
            throw InvalidRequest("stopGeneration must be positive.");
        }

        return await session.InvokeAsync(
            async (client, token) =>
            {
                DebugSessionSnapshot current = await client.GetSessionAsync(token)
                    .ConfigureAwait(false);
                RequireStoppedGeneration(current, stopGeneration);
                return await operation(session, client, token).ConfigureAwait(false);
            },
            cancellationToken).ConfigureAwait(false);
    }

    private static void RequireAgentControl(McpDebuggerSession session)
    {
        if (!session.AgentControl)
        {
            throw new McpDebuggerException(
                "debugger_control_denied",
                $"Debugger session {session.Id} has no agent-control grant.");
        }
    }

    private static void RequireStoppedGeneration(
        DebugSessionSnapshot current,
        long? stopGeneration)
    {
        if (current.State != DebugSessionState.Stopped)
        {
            throw new McpDebuggerException(
                "debugger_invalid_state",
                $"A stopped target is required, not {current.State}.");
        }

        if (stopGeneration != current.StopGeneration)
        {
            throw new McpDebuggerException(
                "debugger_stale_generation",
                $"stopGeneration {stopGeneration} is stale; " +
                $"the current generation is {current.StopGeneration}.");
        }
    }

    private static void ValidatePage(int start, int count, string startName, string countName)
    {
        if (start < 0 || count < 0 || count > MaximumPageSize)
        {
            throw InvalidRequest(
                $"{startName} must be non-negative and {countName} " +
                $"must be between zero and {MaximumPageSize}.");
        }
    }

    private static void ValidatePositive(int value, string name)
    {
        if (value <= 0)
        {
            throw InvalidRequest($"{name} must be positive.");
        }
    }

    private static McpDebuggerException InvalidRequest(string message) =>
        new("debugger_request_invalid", message);
}
