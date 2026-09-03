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

        DebugExpressionPlan plan = await CompileExpressionAsync(
            language,
            expression,
            cancellationToken).ConfigureAwait(false);

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

    private Task<DebugExpressionPlan> CompileExpressionAsync(
        DebugExpressionLanguage language,
        string expression,
        CancellationToken cancellationToken) => language switch
        {
            DebugExpressionLanguage.CSharp or DebugExpressionLanguage.VisualBasic or
            DebugExpressionLanguage.FSharp => _evaluator.CompileAsync(
                new DebugExpressionCompileRequest(language, expression),
                cancellationToken),
            DebugExpressionLanguage.Common => Task.FromResult(
                ManagedSideEffectFreeExpressionParser.Parse(expression, language)),
            _ => throw new InvalidOperationException(
                $"Expression language {language} is unavailable.")
        };

    private CorDebugDebuggee GetStoppedManagedDebuggee() =>
        _state == DebugSessionState.Stopped && _debuggee is CorDebugDebuggee managedDebuggee
            ? managedDebuggee
            : throw new InvalidOperationException(
                $"Managed evaluation is unavailable while the debugger session is {_state}.");
}
