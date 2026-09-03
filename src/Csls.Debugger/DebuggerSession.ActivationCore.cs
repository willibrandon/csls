using Csls.Debugger.Contracts;

namespace Csls.Debugger;

/// <summary>
/// Implements serialized target activation and observation transitions.
/// </summary>
public sealed partial class DebuggerSession
{
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
        if (_pendingStop is PendingDebugStop pendingStop)
        {
            _pendingStop = null;
            _currentException = pendingStop.Exception;
            _currentExceptionThreadId = pendingStop.Exception is null
                ? null
                : pendingStop.ThreadId;
            await EnterStoppedStateAsync(
                pendingStop.Reason,
                pendingStop.ThreadId,
                cancellationToken).ConfigureAwait(false);
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

    private Task DetachDebuggeeAsync(CancellationToken cancellationToken)
    {
        return _actor.InvokeAsync(
            token =>
            {
                _ = token;
                if (_debuggee is null ||
                    _state is DebugSessionState.Terminated or DebugSessionState.Faulted)
                {
                    throw new InvalidOperationException(
                        $"A target cannot be detached while the debugger session is {_state}.");
                }

                DebugSessionState previousState = _state;
                _state = DebugSessionState.Terminating;
                var managedDebuggee = _debuggee as CorDebugDebuggee;
                bool resumeAfterFailure = false;
                try
                {
                    if (managedDebuggee is not null)
                    {
                        resumeAfterFailure = managedDebuggee.PrepareForDetach();
                    }

                    _sourceBreakpoints.ResetRuntimeBindings();
                    _functionBreakpoints.ResetRuntimeBindings();
                    _instructionBreakpoints.ResetRuntimeBindings();
                    _debuggee.Detach();
                }
                catch
                {
                    managedDebuggee?.CancelDetach(resumeAfterFailure);
                    _state = previousState;
                    throw;
                }

                return ValueTask.CompletedTask;
            },
            cancellationToken);
    }

    private ValueTask CompleteDetachCoreAsync(CancellationToken cancellationToken)
    {
        _ = cancellationToken;
        _state = DebugSessionState.Terminated;
        return ValueTask.CompletedTask;
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
