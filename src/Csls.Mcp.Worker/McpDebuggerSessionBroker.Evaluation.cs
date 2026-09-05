using Csls.Debugger.Contracts;

namespace Csls.Mcp.Worker;

/// <summary>
/// Evaluates expressions with separate read-only and target-execution authority.
/// </summary>
internal sealed partial class McpDebuggerSessionBroker
{
    /// <summary>
    /// Gets debugger-presented variables after authorizing target-code execution.
    /// </summary>
    /// <param name="debugSession">The exact debugger-session identifier.</param>
    /// <param name="stopGeneration">The exact current stopped generation.</param>
    /// <param name="variablesReference">The generation-bound variable container.</param>
    /// <param name="start">The zero-based first variable to return.</param>
    /// <param name="count">The maximum number of variables to return.</param>
    /// <param name="cancellationToken">Cancels proxy construction and variable expansion.</param>
    /// <returns>The presented variables and replacement stopped generation.</returns>
    internal Task<McpDebugVariablesResult> GetPresentedVariablesAsync(
        string debugSession,
        long stopGeneration,
        int variablesReference,
        int start,
        int count,
        CancellationToken cancellationToken)
    {
        ValidatePositive(variablesReference, nameof(variablesReference));
        ValidatePage(start, count, nameof(start), nameof(count));
        McpDebuggerSession session = Resolve(debugSession);
        return InvokeControlledStoppedAsync(
            session,
            stopGeneration,
            async (selectedSession, client, token) =>
            {
                IReadOnlyList<DebugVariableInfo> variables = await client.GetVariablesAsync(
                    new DebugVariablesRequest(
                        variablesReference,
                        start,
                        count,
                        AllowTargetCodeExecution: true),
                    token).ConfigureAwait(false);
                DebugSessionSnapshot current = await client.GetSessionAsync(token)
                    .ConfigureAwait(false);
                if (current.State != DebugSessionState.Stopped || current.StopGeneration <= 0)
                {
                    throw new McpDebuggerException(
                        "debugger_invalid_state",
                        $"Debugger presentation left the target in {current.State} state.");
                }

                return new McpDebugVariablesResult(
                    selectedSession.Id,
                    current.StopGeneration,
                    variables);
            },
            cancellationToken);
    }

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

        if (expression.Length > MaximumExpressionLength)
        {
            throw InvalidRequest(
                $"expression must not exceed {MaximumExpressionLength} UTF-16 code units.");
        }
    }
}
