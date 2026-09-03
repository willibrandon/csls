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
    /// Detaches and reattaches a managed target with the latest attach options.
    /// </summary>
    /// <param name="options">The validated replacement attachment.</param>
    /// <param name="cancellationToken">Cancels detachment or activation.</param>
    /// <returns>A task that completes after the replacement attachment starts.</returns>
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
            await ResetTargetForRestartAsync(cancellationToken).ConfigureAwait(false);
            await AttachManagedCoreAsync(options, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _ = _lifecycleGate.Release();
        }
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
