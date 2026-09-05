using Csls.Debugger.Contracts;

namespace Csls.Debugger.Control;

/// <summary>
/// Invokes generation-safe managed assignment operations.
/// </summary>
public sealed partial class DebuggerRpcClient
{
    /// <summary>
    /// Assigns one immediate variable-container child with guarded runtime materialization.
    /// </summary>
    /// <param name="request">The exact generation, container child, and value expression.</param>
    /// <param name="cancellationToken">Cancels compilation or queued runtime access.</param>
    /// <returns>The updated value and stopped generation that owns it.</returns>
    public Task<DebugAssignmentResult> SetVariableAsync(
        DebugSetVariableRequest request,
        CancellationToken cancellationToken) =>
        InvokeAsync<DebugSetVariableRequest, DebugAssignmentResult>(
            DebuggerControlMethods.SetVariable,
            request,
            cancellationToken);

    /// <summary>
    /// Assigns one writable source expression with guarded runtime materialization.
    /// </summary>
    /// <param name="request">The exact generation, frame, target, and value expression.</param>
    /// <param name="cancellationToken">Cancels compilation or queued runtime access.</param>
    /// <returns>The updated value and stopped generation that owns it.</returns>
    public Task<DebugAssignmentResult> SetExpressionAsync(
        DebugSetExpressionRequest request,
        CancellationToken cancellationToken) =>
        InvokeAsync<DebugSetExpressionRequest, DebugAssignmentResult>(
            DebuggerControlMethods.SetExpression,
            request,
            cancellationToken);
}
