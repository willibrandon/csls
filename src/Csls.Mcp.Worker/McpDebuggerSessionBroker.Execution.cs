using Csls.Debugger.Contracts;

namespace Csls.Mcp.Worker;

/// <summary>
/// Applies explicitly authorized debugger execution operations.
/// </summary>
internal sealed partial class McpDebuggerSessionBroker
{
    /// <summary>
    /// Pauses, continues, or steps one explicitly selected debugger session.
    /// </summary>
    /// <param name="debugSession">The exact debugger-session identifier.</param>
    /// <param name="operation">The pause, continue, or step operation.</param>
    /// <param name="stopGeneration">The required current generation for resume operations.</param>
    /// <param name="threadId">The required managed thread for stepping.</param>
    /// <param name="stepKind">The required into, over, or out step kind.</param>
    /// <param name="targetId">The optional generation-bound Step Into target.</param>
    /// <param name="cancellationToken">The MCP request cancellation token.</param>
    /// <returns>The state immediately after the execution request.</returns>
    internal async Task<McpDebugSessionInfo> ExecuteAsync(
        string debugSession,
        string operation,
        long? stopGeneration,
        int? threadId,
        string? stepKind,
        int? targetId,
        CancellationToken cancellationToken)
    {
        McpDebuggerSession session = Resolve(debugSession);
        RequireAgentControl(session);

        DebugSessionSnapshot result = await session.InvokeAsync(
            async (client, token) =>
            {
                DebugSessionSnapshot current = await client.GetSessionAsync(token)
                    .ConfigureAwait(false);
                if (string.Equals(operation, "pause", StringComparison.OrdinalIgnoreCase))
                {
                    RequirePauseArguments(
                        current,
                        stopGeneration,
                        threadId,
                        stepKind,
                        targetId);
                    return await client.PauseAsync(token).ConfigureAwait(false);
                }

                RequireStoppedGeneration(current, stopGeneration);
                if (string.Equals(operation, "continue", StringComparison.OrdinalIgnoreCase))
                {
                    if (threadId is not null || stepKind is not null || targetId is not null)
                    {
                        throw InvalidExecution(
                            "continue does not accept threadId, stepKind, or targetId.");
                    }

                    return await client.ContinueAsync(token).ConfigureAwait(false);
                }

                if (!string.Equals(operation, "step", StringComparison.OrdinalIgnoreCase))
                {
                    throw InvalidExecution("operation must be pause, continue, or step.");
                }

                if (threadId is null or <= 0)
                {
                    throw InvalidExecution("step requires a positive threadId.");
                }

                DebugStepKind kind = ParseStepKind(stepKind);
                if (targetId is <= 0 || targetId is not null && kind != DebugStepKind.Into)
                {
                    throw InvalidExecution(
                        "targetId must be positive and is valid only for Step Into.");
                }

                return await client.StepAsync(
                    new DebugStepRequest(threadId.Value, kind, targetId),
                    token).ConfigureAwait(false);
            },
            cancellationToken).ConfigureAwait(false);
        return McpDebugSessionInfo.Create(
            session.Id,
            session.Kind,
            session.AgentControl,
            result);
    }

    private static void RequirePauseArguments(
        DebugSessionSnapshot current,
        long? stopGeneration,
        int? threadId,
        string? stepKind,
        int? targetId)
    {
        if (current.State != DebugSessionState.Running)
        {
            throw new McpDebuggerException(
                "debugger_invalid_state",
                $"pause requires a running target, not {current.State}.");
        }

        if (stopGeneration is not null || threadId is not null ||
            stepKind is not null || targetId is not null)
        {
            throw InvalidExecution(
                "pause does not accept stopGeneration, threadId, stepKind, or targetId.");
        }
    }

    private static DebugStepKind ParseStepKind(string? stepKind) => stepKind switch
    {
        "into" => DebugStepKind.Into,
        "over" => DebugStepKind.Over,
        "out" => DebugStepKind.Out,
        _ => throw InvalidExecution("stepKind must be into, over, or out.")
    };

    private static McpDebuggerException InvalidExecution(string message) =>
        InvalidRequest(message);
}
