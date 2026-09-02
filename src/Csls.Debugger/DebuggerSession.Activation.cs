using Csls.Debugger.Contracts;

namespace Csls.Debugger;

/// <summary>
/// Starts, attaches, terminates, and detaches debugger targets.
/// </summary>
public sealed partial class DebuggerSession
{
    /// <summary>
    /// Launches and owns a target without activating managed runtime debugging.
    /// </summary>
    /// <param name="options">The validated target launch options.</param>
    /// <param name="cancellationToken">Cancels launch and initial notification.</param>
    /// <returns>A task that completes after the target-start notification is accepted.</returns>
    public async Task LaunchWithoutDebuggingAsync(
        DebuggeeLaunchOptions options,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        ArgumentNullException.ThrowIfNull(options);
        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
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
    /// Launches and owns a target under the native CoreCLR debugger.
    /// </summary>
    /// <param name="options">The validated target launch options.</param>
    /// <param name="cancellationToken">Cancels launch and runtime activation.</param>
    /// <returns>A task that completes after the target-start notification is accepted.</returns>
    public async Task LaunchManagedAsync(
        DebuggeeLaunchOptions options,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        ArgumentNullException.ThrowIfNull(options);
        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _actor.InvokeAsync(BeginLaunchCoreAsync, cancellationToken).ConfigureAwait(false);
            _debuggee = await CorDebugDebuggee.LaunchAsync(
                options,
                _actor,
                _sourceBreakpoints,
                HandleRuntimeBreakpointCoreAsync,
                HandleRuntimeStepCoreAsync,
                cancellationToken)
                .ConfigureAwait(false);
            await _actor.InvokeAsync(
                token => CompleteLaunchCoreAsync(_debuggee, token),
                cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            if (_debuggee is not null)
            {
                await _debuggee.DisposeAsync().ConfigureAwait(false);
                _debuggee = null;
            }

            await _actor.InvokeAsync(
                token =>
                {
                    _sourceBreakpoints.ResetRuntimeBindings();
                    return ResetFailedLaunchCoreAsync(token);
                },
                CancellationToken.None)
                .ConfigureAwait(false);
            throw;
        }
        finally
        {
            _ = _lifecycleGate.Release();
        }
    }

    /// <summary>
    /// Attaches to a running process that has loaded CoreCLR.
    /// </summary>
    /// <param name="processId">The operating-system process identifier.</param>
    /// <param name="cancellationToken">Cancels runtime activation without terminating the target.</param>
    /// <returns>A task that completes after the process notification is accepted.</returns>
    public async Task AttachManagedAsync(int processId, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(processId);
        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _actor.InvokeAsync(BeginLaunchCoreAsync, cancellationToken).ConfigureAwait(false);
            _debuggee = await CorDebugDebuggee.AttachAsync(
                processId,
                _actor,
                _sourceBreakpoints,
                HandleRuntimeBreakpointCoreAsync,
                HandleRuntimeStepCoreAsync,
                cancellationToken).ConfigureAwait(false);
            await _actor.InvokeAsync(
                token => CompleteLaunchCoreAsync(_debuggee, token),
                cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            if (_debuggee is not null)
            {
                await _debuggee.DisposeAsync().ConfigureAwait(false);
                _debuggee = null;
            }

            await _actor.InvokeAsync(
                token =>
                {
                    _sourceBreakpoints.ResetRuntimeBindings();
                    return ResetFailedLaunchCoreAsync(token);
                },
                CancellationToken.None).ConfigureAwait(false);
            throw;
        }
        finally
        {
            _ = _lifecycleGate.Release();
        }
    }

    /// <summary>
    /// Terminates the owned target process and all of its descendants.
    /// </summary>
    /// <param name="cancellationToken">Cancels waiting for process termination.</param>
    /// <returns>A task that completes after final target notifications are delivered.</returns>
    public async Task TerminateAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _actor.InvokeAsync(TerminateCoreAsync, cancellationToken).ConfigureAwait(false);
            Task? debuggeeLifetime = _debuggeeLifetime;
            if (debuggeeLifetime is not null)
            {
                await debuggeeLifetime.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            _ = _lifecycleGate.Release();
        }
    }

    /// <summary>
    /// Detaches from the target without terminating it.
    /// </summary>
    /// <param name="cancellationToken">Cancels waiting for detachment.</param>
    /// <returns>A task that completes after debugger ownership is released.</returns>
    public async Task DetachAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            IDebuggeeProcess debuggee = await BeginDetachAsync(cancellationToken)
                .ConfigureAwait(false);
            await debuggee.DetachAsync(cancellationToken).ConfigureAwait(false);
            await StopObservingDebuggeeAsync().ConfigureAwait(false);
            await _actor.InvokeAsync(
                token =>
                {
                    _ = token;
                    _state = DebugSessionState.Terminated;
                    return ValueTask.CompletedTask;
                },
                CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            _ = _lifecycleGate.Release();
        }
    }

    private async ValueTask LaunchWithoutDebuggingCoreAsync(
        DebuggeeLaunchOptions options,
        CancellationToken cancellationToken)
    {
        if (_state != DebugSessionState.Created)
        {
            throw new InvalidOperationException(
                $"A target cannot be launched while the debugger session is {_state}.");
        }

        await BeginLaunchCoreAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _debuggee = DebuggeeProcess.Start(options);
        }
        catch
        {
            _state = DebugSessionState.Created;

            throw;
        }

        try
        {
            await CompleteLaunchCoreAsync(_debuggee, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await _debuggee.DisposeAsync().ConfigureAwait(false);
            _debuggee = null;
            _state = DebugSessionState.Created;

            throw;
        }
    }

    private ValueTask BeginLaunchCoreAsync(CancellationToken cancellationToken)
    {
        _ = cancellationToken;
        if (_state != DebugSessionState.Created)
        {
            throw new InvalidOperationException(
                $"A target cannot be launched while the debugger session is {_state}.");
        }

        _state = DebugSessionState.Starting;
        return ValueTask.CompletedTask;
    }

    private async ValueTask CompleteLaunchCoreAsync(
        IDebuggeeProcess debuggee,
        CancellationToken cancellationToken)
    {
        _debuggee = debuggee;
        await _observer.OnProcessStartedAsync(
            debuggee.Name,
            debuggee.Id,
            cancellationToken).ConfigureAwait(false);
        _state = DebugSessionState.Running;
        if (_pendingStopThreadId is int threadId)
        {
            _pendingStopThreadId = null;
            await EnterStoppedStateAsync("breakpoint", threadId, cancellationToken)
                .ConfigureAwait(false);
        }

        _debuggeeObservationCancellation?.Dispose();
        _debuggeeObservationCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            _lifetime.Token);
        _debuggeeLifetime = ObserveDebuggeeAsync(
            debuggee,
            _debuggeeObservationCancellation.Token);
    }

    private ValueTask ResetFailedLaunchCoreAsync(CancellationToken cancellationToken)
    {
        _ = cancellationToken;
        if (_debuggee is null && _state == DebugSessionState.Starting)
        {
            _state = DebugSessionState.Created;
        }

        return ValueTask.CompletedTask;
    }

    private async ValueTask TerminateCoreAsync(CancellationToken cancellationToken)
    {
        IDebuggeeProcess? debuggee = _debuggee;
        if (debuggee is null)
        {
            return;
        }

        if (_state is not DebugSessionState.Terminated and not DebugSessionState.Faulted)
        {
            _state = DebugSessionState.Terminating;
        }

        await debuggee.TerminateAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<IDebuggeeProcess> BeginDetachAsync(CancellationToken cancellationToken)
    {
        IDebuggeeProcess? result = null;
        await _actor.InvokeAsync(
            token =>
            {
                _ = token;
                if (_debuggee is null ||
                    _state is DebugSessionState.Terminated or DebugSessionState.Faulted)
                {
                    throw new InvalidOperationException(
                        $"A target cannot be detached while the debugger session is {_state}.");
                }

                _state = DebugSessionState.Terminating;
                result = _debuggee;
                return ValueTask.CompletedTask;
            },
            cancellationToken).ConfigureAwait(false);
        return result!;
    }

    private async Task StopObservingDebuggeeAsync()
    {
        if (_debuggeeObservationCancellation is null)
        {
            return;
        }

        await _debuggeeObservationCancellation.CancelAsync().ConfigureAwait(false);
        if (_debuggeeLifetime is not null)
        {
            try
            {
                await _debuggeeLifetime.WaitAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (
                _debuggeeObservationCancellation.IsCancellationRequested)
            {
            }
        }
    }

}
