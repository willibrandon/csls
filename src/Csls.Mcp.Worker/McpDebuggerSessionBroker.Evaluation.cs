using Csls.Debugger.Contracts;

namespace Csls.Mcp.Worker;

/// <summary>
/// Evaluates expressions with separate read-only and target-execution authority.
/// </summary>
internal sealed partial class McpDebuggerSessionBroker
{
    /// <summary>
    /// Executes an expression after validating control authority and stopped generation.
    /// </summary>
    /// <param name="debugSession">The exact debugger-session identifier.</param>
    /// <param name="stopGeneration">The exact current stopped generation.</param>
    /// <param name="frameId">The generation-bound managed frame handle.</param>
    /// <param name="expression">The expression whose target code may execute.</param>
    /// <param name="cancellationToken">Cancels execution and requests cooperative target recovery.</param>
    /// <returns>The result and new stopped generation after execution.</returns>
    internal Task<McpDebugEvaluationResult> ExecuteExpressionAsync(
        string debugSession,
        long stopGeneration,
        int frameId,
        string expression,
        CancellationToken cancellationToken)
    {
        ValidatePositive(frameId, nameof(frameId));
        ValidateExpression(expression);
        McpDebuggerSession session = Resolve(debugSession);
        return InvokeControlledStoppedAsync(
            session,
            stopGeneration,
            async (selectedSession, client, token) =>
            {
                DebugEvaluateResult evaluation = await client.ExecuteExpressionAsync(
                    new DebugExecuteExpressionRequest(frameId, expression),
                    token).ConfigureAwait(false);
                DebugSessionSnapshot current = await client.GetSessionAsync(token)
                    .ConfigureAwait(false);
                if (current.State != DebugSessionState.Stopped || current.StopGeneration <= 0)
                {
                    throw new McpDebuggerException(
                        "debugger_invalid_state",
                        $"Expression execution left the target in {current.State} state.");
                }

                return new McpDebugEvaluationResult(
                    selectedSession.Id,
                    current.StopGeneration,
                    evaluation);
            },
            cancellationToken);
    }

    private static void ValidateExpression(string expression)
    {
        if (string.IsNullOrWhiteSpace(expression))
        {
            throw InvalidRequest("expression must not be empty.");
        }
    }
}
