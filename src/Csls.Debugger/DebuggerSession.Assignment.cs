using Csls.Debugger.Contracts;

namespace Csls.Debugger;

/// <summary>
/// Exposes language-aware generation-safe managed value assignment.
/// </summary>
public sealed partial class DebuggerSession
{
    /// <summary>
    /// Assigns an expression to one child and safely materializes runtime values when required.
    /// </summary>
    /// <param name="variablesReference">The generation-bound parent container.</param>
    /// <param name="name">The immediate child name.</param>
    /// <param name="value">The source-language value expression.</param>
    /// <param name="generation">The exact stopped generation authorizing the write.</param>
    /// <param name="cancellationToken">Cancels compilation or queued runtime access.</param>
    /// <returns>The assignment result and stopped generation that owns it.</returns>
    public async Task<DebugAssignmentResult> SetVariableAsync(
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
    /// Assigns a value to one writable source expression with guarded runtime materialization.
    /// </summary>
    /// <param name="frameId">The generation-bound managed frame.</param>
    /// <param name="expression">The writable source expression.</param>
    /// <param name="value">The source-language value expression.</param>
    /// <param name="generation">The exact stopped generation authorizing the write.</param>
    /// <param name="cancellationToken">Cancels compilation or queued runtime access.</param>
    /// <returns>The assignment result and stopped generation that owns it.</returns>
    public async Task<DebugAssignmentResult> SetExpressionAsync(
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

    private async Task<DebugAssignmentResult> SetExpressionCoreAsync(
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
        Task<ManagedFunctionEvaluationResult>? functionEvaluation = null;
        CorDebugDebuggee? evaluationDebuggee = null;
        ManagedFrameSelection? frameSelection = null;
        ManagedStringAssignmentPlan? stringAssignment = null;
        await _actor.InvokeAsync(
            token =>
            {
                _ = token;
                CorDebugDebuggee debuggee = GetAssignmentDebuggee(generation);
                bool isInvocation = value.Root.Kind is
                    DebugExpressionNodeKind.Invocation or
                    DebugExpressionNodeKind.ObjectCreation;
                stringAssignment = isInvocation ? null : debuggee.CreateStringMaterializationPlan(
                    frameId, target, value, targetExpression, generation);
                DebugExpressionPlan? executionPlan = isInvocation ? value : stringAssignment?.Plan;
                if (executionPlan is null)
                {
                    result = debuggee.SetExpression(
                        frameId,
                        target,
                        value,
                        targetExpression,
                        resultName,
                        generation,
                        _variableMutations);
                }
                else
                {
                    evaluationDebuggee = debuggee;
                    frameSelection = debuggee.CaptureFrameSelection(frameId, generation);
                    try
                    {
                        functionEvaluation = debuggee.BeginFunctionEvaluationAsync(
                            frameId,
                            executionPlan,
                            generation);
                        _state = DebugSessionState.Running;
                    }
                    catch (Exception exception) when (
                        debuggee.FunctionEvaluationSafetyFailure is string reason)
                    {
                        _stopGeneration = _stopGeneration.Next();
                        _state = DebugSessionState.Faulted;
                        throw new InvalidOperationException(reason, exception);
                    }
                }

                return ValueTask.CompletedTask;
            },
            cancellationToken).ConfigureAwait(false);
        if (functionEvaluation is null)
        {
            return new DebugAssignmentResult(
                generation.Value,
                TargetCodeExecuted: false,
                result!);
        }

        ManagedFunctionEvaluationResult evaluation = await WaitForFunctionEvaluationAsync(
            evaluationDebuggee!,
            functionEvaluation,
            cancellationToken).ConfigureAwait(false);
        if (stringAssignment is not null)
        {
            evaluation = evaluation with { DeclaredType = stringAssignment.DeclaredType };
        }

        await _actor.InvokeAsync(
            token =>
            {
                CorDebugDebuggee debuggee = GetAssignmentDebuggee(evaluation.Generation);
                int replacementFrameId = debuggee.ReacquireFrame(
                    frameSelection!,
                    evaluation.Generation,
                    token);
                result = debuggee.SetExpressionFromEvaluation(
                    replacementFrameId,
                    target,
                    evaluation,
                    targetExpression,
                    resultName,
                    _variableMutations);
                return ValueTask.CompletedTask;
            },
            cancellationToken).ConfigureAwait(false);
        return new DebugAssignmentResult(
            evaluation.Generation.Value,
            TargetCodeExecuted: true,
            result!);
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
