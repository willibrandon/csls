namespace Csls.Debugger;

/// <summary>
/// Launches debugger-owned managed targets.
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
            await _actor.InvokeAsync(
                token => BeginManagedLaunchAsync(options, token),
                cancellationToken).ConfigureAwait(false);
            _debuggee = await CorDebugDebuggee.LaunchAsync(
                options,
                _actor,
                _sourceBreakpoints,
                _functionBreakpoints,
                HandleRuntimeBreakpointCoreAsync,
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
        finally
        {
            _ = _lifecycleGate.Release();
        }
    }

    private ValueTask BeginManagedLaunchAsync(
        DebuggeeLaunchOptions options,
        CancellationToken cancellationToken)
    {
        _sourceBreakpoints.SetSourceOptions(options.SourceFileMap, options.SourceLinkOptions);
        _sourceBreakpoints.SetRuntimeOptions(
            options.SuppressJitOptimizations,
            options.JustMyCode,
            options.EnableStepFiltering);
        return BeginLaunchCoreAsync(cancellationToken);
    }

    private async Task ResetFailedManagedLaunchAsync()
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
                return ResetFailedLaunchCoreAsync(token);
            },
            CancellationToken.None).ConfigureAwait(false);
    }
}
