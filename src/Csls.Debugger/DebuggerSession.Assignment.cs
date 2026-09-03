using Csls.Debugger.Contracts;

namespace Csls.Debugger;

/// <summary>
/// Exposes language-aware generation-safe managed value assignment.
/// </summary>
public sealed partial class DebuggerSession
{
    /// <summary>
    /// Assigns a side-effect-free expression to one child of a variable container.
    /// </summary>
    /// <param name="variablesReference">The generation-bound parent container.</param>
    /// <param name="name">The immediate child name.</param>
    /// <param name="value">The source-language value expression.</param>
    /// <param name="generation">The exact stopped generation authorizing the write.</param>
    /// <param name="cancellationToken">Cancels compilation or queued runtime access.</param>
    /// <returns>The updated immediate variable.</returns>
    public async Task<DebugVariableInfo> SetVariableAsync(
        int variablesReference,
        string name,
        string value,
        DebugStopGeneration generation,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(variablesReference);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        int frameId = 0;
        string? targetExpression = null;
        DebugExpressionLanguage language = default;
        await _actor.InvokeAsync(
            token =>
            {
                _ = token;
                CorDebugDebuggee debuggee = GetAssignmentDebuggee(generation);
                (frameId, targetExpression) = debuggee.GetVariableAssignmentTarget(
                    variablesReference,
                    name,
                    generation);
                language = debuggee.GetExpressionLanguage(frameId, generation);
                return ValueTask.CompletedTask;
            },
            cancellationToken).ConfigureAwait(false);

        return await SetExpressionCoreAsync(
            frameId,
            targetExpression!,
            value,
            name,
            language,
            generation,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Assigns a side-effect-free expression to one writable source expression.
    /// </summary>
    /// <param name="frameId">The generation-bound managed frame.</param>
    /// <param name="expression">The writable source expression.</param>
    /// <param name="value">The source-language value expression.</param>
    /// <param name="generation">The exact stopped generation authorizing the write.</param>
    /// <param name="cancellationToken">Cancels compilation or queued runtime access.</param>
    /// <returns>The updated immediate variable.</returns>
    public async Task<DebugVariableInfo> SetExpressionAsync(
        int frameId,
        string expression,
        string value,
        DebugStopGeneration generation,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(frameId);
        ArgumentException.ThrowIfNullOrWhiteSpace(expression);
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        DebugExpressionLanguage language = default;
        await _actor.InvokeAsync(
            token =>
            {
                _ = token;
                CorDebugDebuggee debuggee = GetAssignmentDebuggee(generation);
                language = debuggee.GetExpressionLanguage(frameId, generation);
                return ValueTask.CompletedTask;
            },
            cancellationToken).ConfigureAwait(false);

        return await SetExpressionCoreAsync(
            frameId,
            expression,
            value,
            expression,
            language,
            generation,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<DebugVariableInfo> SetExpressionCoreAsync(
        int frameId,
        string targetExpression,
        string valueExpression,
        string resultName,
        DebugExpressionLanguage language,
        DebugStopGeneration generation,
        CancellationToken cancellationToken)
    {
        DebugExpressionPlan target = await CompileExpressionAsync(
            language,
            targetExpression,
            cancellationToken).ConfigureAwait(false);
        DebugExpressionPlan value = await CompileExpressionAsync(
            language,
            valueExpression,
            cancellationToken).ConfigureAwait(false);
        DebugVariableInfo? result = null;
        await _actor.InvokeAsync(
            token =>
            {
                _ = token;
                CorDebugDebuggee debuggee = GetAssignmentDebuggee(generation);
                result = debuggee.SetExpression(
                    frameId,
                    target,
                    value,
                    targetExpression,
                    resultName,
                    generation);
                return ValueTask.CompletedTask;
            },
            cancellationToken).ConfigureAwait(false);
        return result!;
    }

    private CorDebugDebuggee GetAssignmentDebuggee(DebugStopGeneration generation)
    {
        CorDebugDebuggee debuggee = GetStoppedManagedDebuggee();
        if (_stopGeneration != generation)
        {
            throw new InvalidOperationException(
                $"Assignment generation {generation.Value} is stale; the current stopped " +
                $"generation is {_stopGeneration.Value}.");
        }

        return debuggee;
    }
}
