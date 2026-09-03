using Csls.Debugger.Contracts;

namespace Csls.Debugger;

/// <summary>
/// Exposes generation-bound managed expression evaluation.
/// </summary>
public sealed partial class DebuggerSession
{
    /// <summary>
    /// Evaluates one side-effect-free expression in a stopped managed frame.
    /// </summary>
    /// <param name="frameId">The generation-bound managed frame handle.</param>
    /// <param name="expression">The source expression to evaluate.</param>
    /// <param name="cancellationToken">Cancels queueing expression evaluation.</param>
    /// <returns>The current formatted expression result.</returns>
    public async Task<DebugEvaluateResult> EvaluateAsync(
        int frameId,
        string expression,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(expression);
        DebugEvaluateResult? result = null;
        await _actor.InvokeAsync(
            token =>
            {
                _ = token;
                if (_state != DebugSessionState.Stopped ||
                    _debuggee is not CorDebugDebuggee managedDebuggee)
                {
                    throw new InvalidOperationException(
                        $"Managed evaluation is unavailable while the debugger session is {_state}.");
                }

                result = managedDebuggee.Evaluate(frameId, expression, _stopGeneration);
                return ValueTask.CompletedTask;
            },
            cancellationToken).ConfigureAwait(false);
        return result!;
    }
}
