namespace Csls.Debugger;

/// <summary>
/// Starts, attaches, terminates, and detaches debugger targets.
/// </summary>
public sealed partial class DebuggerSession
{
    /// <summary>
    /// Attaches to a running process that has loaded CoreCLR.
    /// </summary>
    /// <param name="options">The validated target and runtime options.</param>
    /// <param name="cancellationToken">Cancels runtime activation without terminating the target.</param>
    /// <returns>A task that completes after the process notification is accepted.</returns>
    public async Task AttachManagedAsync(
        DebuggeeAttachOptions options,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.ProcessId);
        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await AttachManagedCoreAsync(options, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _ = _lifecycleGate.Release();
        }
    }

    private async Task AttachManagedCoreAsync(
        DebuggeeAttachOptions options,
        CancellationToken cancellationToken)
    {
        try
        {
            await _actor.InvokeAsync(
                token => BeginManagedAttachAsync(options, token),
                cancellationToken).ConfigureAwait(false);
            _debuggee = await CorDebugDebuggee.AttachAsync(
                options.ProcessId,
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
            await ResetFailedManagedLaunchAsync().ConfigureAwait(false);
            throw;
        }
    }

    private ValueTask BeginManagedAttachAsync(
        DebuggeeAttachOptions options,
        CancellationToken cancellationToken)
    {
        _sourceBreakpoints.SetSourceOptions(
            options.SourceFileMap,
            options.SourceLinkOptions,
            options.SymbolOptions);
        _sourceBreakpoints.SetRuntimeOptions(
            suppressJitOptimizations: false,
            options.JustMyCode,
            options.EnableStepFiltering);
        return BeginLaunchCoreAsync(cancellationToken);
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
            await DetachDebuggeeAsync(cancellationToken).ConfigureAwait(false);
            await StopObservingDebuggeeAsync().ConfigureAwait(false);
            await _actor.InvokeAsync(CompleteDetachCoreAsync, CancellationToken.None)
                .ConfigureAwait(false);
        }
        finally
        {
            _ = _lifecycleGate.Release();
        }
    }

}
