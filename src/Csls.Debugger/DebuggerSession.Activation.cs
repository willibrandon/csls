using Csls.Debugger.Contracts;

namespace Csls.Debugger;

/// <summary>
/// Starts, attaches, terminates, and detaches debugger targets.
/// </summary>
public sealed partial class DebuggerSession
{
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
                _functionBreakpoints,
                _instructionBreakpoints,
                HandleRuntimeBreakpointCoreAsync,
                HandleRuntimeTargetBreakpointCoreAsync,
                HandleRuntimeStepCoreAsync,
                HandleRuntimeExceptionCoreAsync,
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
                    _functionBreakpoints.ResetRuntimeBindings();
                    _instructionBreakpoints.ResetRuntimeBindings();
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

}
