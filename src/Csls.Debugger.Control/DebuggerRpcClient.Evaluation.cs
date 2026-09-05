using Csls.Debugger.Contracts;

namespace Csls.Debugger.Control;

/// <summary>
/// Invokes read-only and target-executing debugger expression operations.
/// </summary>
public sealed partial class DebuggerRpcClient
{
    /// <summary>
    /// Evaluates an expression in a stopped managed frame without executing target code.
    /// </summary>
    /// <param name="request">The selected frame and expression.</param>
    /// <param name="cancellationToken">Cancels evaluation.</param>
    /// <returns>The formatted expression result.</returns>
    public Task<DebugEvaluateResult> EvaluateAsync(
        DebugEvaluateRequest request,
        CancellationToken cancellationToken) =>
        InvokeAsync<DebugEvaluateRequest, DebugEvaluateResult>(
            DebuggerControlMethods.Evaluate,
            request,
            cancellationToken);

    /// <summary>
    /// Executes an explicitly authorized expression in a stopped managed frame.
    /// </summary>
    /// <param name="request">The selected frame and expression.</param>
    /// <param name="cancellationToken">Cancels execution and requests cooperative target recovery.</param>
    /// <returns>The formatted expression result.</returns>
    public Task<DebugEvaluateResult> ExecuteExpressionAsync(
        DebugExecuteExpressionRequest request,
        CancellationToken cancellationToken) =>
        InvokeAsync<DebugExecuteExpressionRequest, DebugEvaluateResult>(
            DebuggerControlMethods.ExecuteExpression,
            request,
            cancellationToken);
}
