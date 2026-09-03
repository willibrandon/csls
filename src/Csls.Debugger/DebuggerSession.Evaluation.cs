using Csls.Debugger.Contracts;

namespace Csls.Debugger;

/// <summary>
/// Exposes language-aware generation-bound managed expression evaluation.
/// </summary>
public sealed partial class DebuggerSession
{
    /// <summary>
    /// Evaluates one side-effect-free expression in a stopped managed frame.
    /// </summary>
    /// <param name="frameId">The generation-bound managed frame handle.</param>
    /// <param name="expression">The source-language expression to evaluate.</param>
    /// <param name="cancellationToken">Cancels expression binding or runtime inspection.</param>
    /// <returns>The current formatted expression result.</returns>
    public async Task<DebugEvaluateResult> EvaluateAsync(
        int frameId,
        string expression,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(expression);
        DebugExpressionLanguage language = default;
        DebugStopGeneration generation = default;
        await _actor.InvokeAsync(
            token =>
            {
                _ = token;
                CorDebugDebuggee managedDebuggee = GetStoppedManagedDebuggee();
                generation = _stopGeneration;
                language = managedDebuggee.GetExpressionLanguage(frameId, generation);
                return ValueTask.CompletedTask;
            },
            cancellationToken).ConfigureAwait(false);

        DebugExpressionPlan plan = language switch
        {
            DebugExpressionLanguage.CSharp or DebugExpressionLanguage.VisualBasic =>
                await _evaluator.CompileAsync(
                    new DebugExpressionCompileRequest(language, expression),
                    cancellationToken).ConfigureAwait(false),
            DebugExpressionLanguage.Common or DebugExpressionLanguage.FSharp =>
                ManagedSideEffectFreeExpressionParser.Parse(expression, language),
            _ => throw new InvalidOperationException(
                $"Expression language {language} is unavailable.")
        };

        DebugEvaluateResult? result = null;
        await _actor.InvokeAsync(
            token =>
            {
                _ = token;
                CorDebugDebuggee managedDebuggee = GetStoppedManagedDebuggee();
                result = managedDebuggee.Evaluate(frameId, plan, generation);
                return ValueTask.CompletedTask;
            },
            cancellationToken).ConfigureAwait(false);
        return result!;
    }

    private CorDebugDebuggee GetStoppedManagedDebuggee() =>
        _state == DebugSessionState.Stopped && _debuggee is CorDebugDebuggee managedDebuggee
            ? managedDebuggee
            : throw new InvalidOperationException(
                $"Managed evaluation is unavailable while the debugger session is {_state}.");
}
