using Csls.Debugger.Contracts;

namespace Csls.Debugger.Control;

/// <summary>
/// Invokes generation-safe managed assignment operations.
/// </summary>
public sealed partial class DebuggerRpcClient
{
    /// <summary>
    /// Assigns one immediate variable-container child without executing target code.
    /// </summary>
    /// <param name="request">The exact generation, container child, and value expression.</param>
    /// <param name="cancellationToken">Cancels compilation or queued runtime access.</param>
    /// <returns>The updated immediate variable.</returns>
    public Task<DebugVariableInfo> SetVariableAsync(
        DebugSetVariableRequest request,
        CancellationToken cancellationToken) =>
        InvokeAsync<DebugSetVariableRequest, DebugVariableInfo>(
            DebuggerControlMethods.SetVariable,
            request,
            cancellationToken);

    /// <summary>
    /// Assigns one writable source expression without executing target code.
    /// </summary>
    /// <param name="request">The exact generation, frame, target, and value expression.</param>
    /// <param name="cancellationToken">Cancels compilation or queued runtime access.</param>
    /// <returns>The updated immediate variable.</returns>
    public Task<DebugVariableInfo> SetExpressionAsync(
        DebugSetExpressionRequest request,
        CancellationToken cancellationToken) =>
        InvokeAsync<DebugSetExpressionRequest, DebugVariableInfo>(
            DebuggerControlMethods.SetExpression,
            request,
            cancellationToken);
}
