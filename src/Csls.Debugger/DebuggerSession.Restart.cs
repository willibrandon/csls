using Csls.Debugger.Contracts;

namespace Csls.Debugger;

/// <summary>
/// Restarts debugger targets while preserving logical session policy.
/// </summary>
public sealed partial class DebuggerSession
{
    /// <summary>
    /// Restarts a debugger-owned managed target with the latest launch options.
    /// </summary>
    /// <param name="options">The validated replacement target launch.</param>
    /// <param name="cancellationToken">Cancels target shutdown or activation.</param>
    /// <returns>A task that completes after the replacement target starts.</returns>
    public async Task RestartManagedAsync(
        DebuggeeLaunchOptions options,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        ArgumentNullException.ThrowIfNull(options);
        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await ResetTargetForRestartAsync(cancellationToken).ConfigureAwait(false);
            await LaunchManagedCoreAsync(options, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _ = _lifecycleGate.Release();
        }
    }

    /// <summary>
    /// Restarts a debugger-owned target without managed runtime activation.
    /// </summary>
    /// <param name="options">The validated replacement target launch.</param>
    /// <param name="cancellationToken">Cancels target shutdown or activation.</param>
    /// <returns>A task that completes after the replacement target starts.</returns>
    public async Task RestartWithoutDebuggingAsync(
        DebuggeeLaunchOptions options,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        ArgumentNullException.ThrowIfNull(options);
        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await ResetTargetForRestartAsync(cancellationToken).ConfigureAwait(false);
            await _actor.InvokeAsync(
                token => LaunchWithoutDebuggingCoreAsync(options, token),
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _ = _lifecycleGate.Release();
        }
    }

    /// <summary>
    /// Restarts an attached session without disconnecting from the live target.
    /// </summary>
    /// <param name="options">The validated replacement attachment.</param>
    /// <param name="cancellationToken">Cancels restarting or replacement attachment.</param>
    /// <returns>A task that completes after the attachment resumes.</returns>
    public async Task RestartManagedAttachAsync(
        DebuggeeAttachOptions options,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.ProcessId);
        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_debuggee is CorDebugDebuggee attachedDebuggee &&
                !attachedDebuggee.OwnsProcess &&
                attachedDebuggee.Id == options.ProcessId &&
                _state is DebugSessionState.Running or DebugSessionState.Stopped)
            {
                await _actor.InvokeAsync(
                    token => RestartAttachedProcessCoreAsync(attachedDebuggee, options, token),
                    cancellationToken).ConfigureAwait(false);
                return;
            }

            await ResetTargetForRestartAsync(cancellationToken).ConfigureAwait(false);
            await AttachManagedCoreAsync(options, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _ = _lifecycleGate.Release();
        }
    }

    private async ValueTask RestartAttachedProcessCoreAsync(
        CorDebugDebuggee debuggee,
        DebuggeeAttachOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options.ExpressionEvaluationOptions);
        if (options.JustMyCode != _attachedJustMyCode ||
            options.EnableStepFiltering != _attachedEnableStepFiltering)
        {
            throw new InvalidOperationException(
                "Just My Code and step filtering cannot change while attached to a live process. Disconnect before attaching with different runtime options.");
        }

        debuggee.CancelStep();
        if (_state == DebugSessionState.Stopped)
        {
            debuggee.Continue();
        }
        else
        {
            debuggee.DiscardBreakpointInspection();
        }

        _sourceBreakpoints.SetSourceOptions(
            options.SourceFileMap,
            options.SourceLinkOptions,
            options.SymbolOptions,
            options.RequireExactSource);
        debuggee.SetExpressionEvaluationOptions(options.ExpressionEvaluationOptions);
        _stopGeneration = _stopGeneration.Value == 0
            ? DebugStopGeneration.First
            : _stopGeneration.Next();
        _pendingStop = null;
        _currentException = null;
        _currentExceptionThreadId = null;
        _state = DebugSessionState.Running;
        await _observer.OnProcessStartedAsync(
            debuggee.Name,
            debuggee.Id,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task ResetTargetForRestartAsync(CancellationToken cancellationToken)
    {
        IDebuggeeProcess debuggee = _debuggee ?? throw new InvalidOperationException(
            "A debugger target must be activated before it can be restarted.");
        if (_state is DebugSessionState.Created or DebugSessionState.Starting or
            DebugSessionState.Terminating)
        {
            throw new InvalidOperationException(
                $"A debugger target cannot be restarted while the session is {_state}.");
        }

        if (_state is not DebugSessionState.Terminated and not DebugSessionState.Faulted)
        {
            if (debuggee.OwnsProcess)
            {
                await _actor.InvokeAsync(TerminateCoreAsync, cancellationToken)
                    .ConfigureAwait(false);
                if (_debuggeeLifetime is not null)
                {
                    await _debuggeeLifetime.WaitAsync(cancellationToken).ConfigureAwait(false);
                }
            }
            else
            {
                await DetachDebuggeeAsync(cancellationToken).ConfigureAwait(false);
                await StopObservingDebuggeeAsync().ConfigureAwait(false);
                await _actor.InvokeAsync(
                    CompleteDetachCoreAsync,
                    CancellationToken.None).ConfigureAwait(false);
            }
        }
        else if (_debuggeeLifetime is not null)
        {
            await _debuggeeLifetime.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        await _actor.InvokeAsync(
            token =>
            {
                _ = token;
                _sourceBreakpoints.ResetRuntimeBindings();
                _functionBreakpoints.ResetRuntimeBindings();
                _instructionBreakpoints.ResetRuntimeBindings();
                _entryBreakpoint.Reset();
                _pendingStop = null;
                _currentException = null;
                _currentExceptionThreadId = null;
                return ValueTask.CompletedTask;
            },
            CancellationToken.None).ConfigureAwait(false);
        await debuggee.DisposeAsync().ConfigureAwait(false);
        _debuggeeObservationCancellation?.Dispose();
        _debuggeeObservationCancellation = null;
        _debuggeeLifetime = null;
        _debuggee = null;
        await _actor.InvokeAsync(
            token =>
            {
                _ = token;
                _state = DebugSessionState.Created;
                return ValueTask.CompletedTask;
            },
            CancellationToken.None).ConfigureAwait(false);
    }
}
