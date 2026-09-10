using Csls.Debugger.Contracts;

namespace Csls.Mcp.Worker;

/// <summary>
/// Applies explicitly authorized managed assignments at exact stopped generations.
/// </summary>
internal sealed partial class McpDebuggerSessionBroker
{
    /// <summary>
    /// Assigns one variable-container child after validating control authority.
    /// </summary>
    /// <param name="debugSession">The exact debugger-session identifier.</param>
    /// <param name="stopGeneration">The exact current stopped generation.</param>
    /// <param name="variablesReference">The generation-bound parent container.</param>
    /// <param name="name">The immediate child name.</param>
    /// <param name="value">The source-language value expression to assign.</param>
    /// <param name="cancellationToken">Cancels compilation or queued runtime access.</param>
    /// <returns>The updated value and stopped generation that owns it.</returns>
    internal Task<McpDebugAssignmentResult> SetVariableAsync(
        string debugSession,
        long stopGeneration,
        int variablesReference,
        string name,
        string value,
        CancellationToken cancellationToken)
    {
        ValidatePositive(variablesReference, nameof(variablesReference));
        ValidateExpression(name);
        ValidateExpression(value);
        McpDebuggerSession session = Resolve(debugSession);
        return InvokeControlledStoppedAsync(
            session,
            stopGeneration,
            async (selectedSession, client, token) =>
            {
                DebugAssignmentResult assignment = await client.SetVariableAsync(
                    new DebugSetVariableRequest(
                        stopGeneration,
                        variablesReference,
                        name,
                        value),
                    token).ConfigureAwait(false);
                return new McpDebugAssignmentResult(
                    selectedSession.Id,
                    assignment.StopGeneration,
                    assignment.TargetCodeExecuted,
                    assignment.Variable);
            },
            cancellationToken);
    }

    /// <summary>
    /// Assigns one source expression after validating control authority.
    /// </summary>
    /// <param name="debugSession">The exact debugger-session identifier.</param>
    /// <param name="stopGeneration">The exact current stopped generation.</param>
    /// <param name="frameId">The generation-bound managed frame.</param>
    /// <param name="expression">The writable source expression.</param>
    /// <param name="value">The source-language value expression to assign.</param>
    /// <param name="cancellationToken">Cancels compilation or queued runtime access.</param>
    /// <returns>The updated value and stopped generation that owns it.</returns>
    internal Task<McpDebugAssignmentResult> SetExpressionAsync(
        string debugSession,
        long stopGeneration,
        int frameId,
        string expression,
        string value,
        CancellationToken cancellationToken)
    {
        ValidatePositive(frameId, nameof(frameId));
        ValidateExpression(expression);
        ValidateExpression(value);
        McpDebuggerSession session = Resolve(debugSession);
        return InvokeControlledStoppedAsync(
            session,
            stopGeneration,
            async (selectedSession, client, token) =>
            {
                DebugAssignmentResult assignment = await client.SetExpressionAsync(
                    new DebugSetExpressionRequest(
                        stopGeneration,
                        frameId,
                        expression,
                        value),
                    token).ConfigureAwait(false);
                return new McpDebugAssignmentResult(
                    selectedSession.Id,
                    assignment.StopGeneration,
                    assignment.TargetCodeExecuted,
                    assignment.Variable);
            },
            cancellationToken);
    }
}
