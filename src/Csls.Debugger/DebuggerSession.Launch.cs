using Csls.Debugger.Contracts;

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
    /// Observes a debugger-owned target whose standard handles belong to a client terminal.
    /// </summary>
    /// <param name="options">The validated target launch policy.</param>
    /// <param name="processId">The authenticated child identifier reported by the launcher.</param>
    /// <param name="terminalExitCode">Reads the child's final direct-parent exit report.</param>
    /// <param name="cancellationToken">Cancels target activation.</param>
    /// <returns>A task that completes after the target-start notification is accepted.</returns>
    public async Task LaunchWithoutDebuggingInTerminalAsync(
        DebuggeeLaunchOptions options,
        int processId,
        Func<CancellationToken, Task<int>> terminalExitCode,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(terminalExitCode);
        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _actor.InvokeAsync(
                token => LaunchWithoutDebuggingInTerminalCoreAsync(
                    options, processId, terminalExitCode, token),
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
            await LaunchManagedCoreAsync(options, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _ = _lifecycleGate.Release();
        }
    }

    /// <summary>
    /// Activates an authenticated terminal child held at CoreCLR's diagnostic startup gate.
    /// </summary>
    /// <param name="options">The validated target launch policy.</param>
    /// <param name="processId">The actual child identifier reported by the launcher.</param>
    /// <param name="terminalExitCode">Reads the final exit code from the child's direct parent.</param>
    /// <param name="cancellationToken">Cancels activation and terminates the owned target.</param>
    /// <returns>A task that completes after the target-start notification is accepted.</returns>
    public async Task LaunchManagedInTerminalAsync(
        DebuggeeLaunchOptions options,
        int processId,
        Func<CancellationToken, Task<int>> terminalExitCode,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(terminalExitCode);
        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await LaunchManagedInTerminalCoreAsync(
                options, processId, terminalExitCode, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _ = _lifecycleGate.Release();
        }
    }

    private async Task LaunchManagedInTerminalCoreAsync(
        DebuggeeLaunchOptions options,
        int processId,
        Func<CancellationToken, Task<int>> terminalExitCode,
        CancellationToken cancellationToken)
    {
        try
        {
            await _actor.InvokeAsync(
                token => BeginManagedLaunchAsync(options, token),
                cancellationToken).ConfigureAwait(false);
            CorDebugDebuggee debuggee = await CorDebugDebuggee.LaunchTerminalAsync(
                options,
                processId,
                terminalExitCode,
                _actor,
                _observer,
                _sourceBreakpoints,
                _functionBreakpoints,
                _instructionBreakpoints,
                _entryBreakpoint,
                HandleRuntimeBreakpointCoreAsync,
                HandleRuntimeTargetBreakpointCoreAsync,
                HandleRuntimeStepCoreAsync,
                HandleRuntimeBreakRequestCoreAsync,
                HandleRuntimeExceptionCoreAsync,
                HandleRuntimeEvaluationCoreAsync,
                cancellationToken).ConfigureAwait(false);
            _debuggee = debuggee;
            await _actor.InvokeAsync(
                token =>
                {
                    debuggee.SetExpressionEvaluationOptions(options.ExpressionEvaluationOptions);
                    return CompleteLaunchCoreAsync(debuggee, token);
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch (CorDebugRuntimeException)
        {
            await ResetFailedManagedLaunchAsync(runtimeAvailable: false).ConfigureAwait(false);
            throw;
        }
        catch
        {
            await ResetFailedManagedLaunchAsync().ConfigureAwait(false);
            throw;
        }
    }

    private async Task LaunchManagedCoreAsync(
        DebuggeeLaunchOptions options,
        CancellationToken cancellationToken)
    {
        try
        {
            await _actor.InvokeAsync(
                token => BeginManagedLaunchAsync(options, token),
                cancellationToken).ConfigureAwait(false);
            CorDebugDebuggee debuggee = await CorDebugDebuggee.LaunchAsync(
                options,
                _actor,
                _observer,
                _sourceBreakpoints,
                _functionBreakpoints,
                _instructionBreakpoints,
                _entryBreakpoint,
                HandleRuntimeBreakpointCoreAsync,
                HandleRuntimeTargetBreakpointCoreAsync,
                HandleRuntimeStepCoreAsync,
                HandleRuntimeBreakRequestCoreAsync,
                HandleRuntimeExceptionCoreAsync,
                HandleRuntimeEvaluationCoreAsync,
                cancellationToken).ConfigureAwait(false);
            _debuggee = debuggee;
            await _actor.InvokeAsync(
                token =>
                {
                    debuggee.SetExpressionEvaluationOptions(options.ExpressionEvaluationOptions);
                    return CompleteLaunchCoreAsync(debuggee, token);
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch (CorDebugRuntimeException)
        {
            await ResetFailedManagedLaunchAsync(runtimeAvailable: false).ConfigureAwait(false);
            throw;
        }
        catch
        {
            await ResetFailedManagedLaunchAsync().ConfigureAwait(false);
            throw;
        }
    }

    private ValueTask BeginManagedLaunchAsync(
        DebuggeeLaunchOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options.ExpressionEvaluationOptions);
        _entryBreakpoint.Configure(options.StopAtEntry);
        _sourceBreakpoints.SetSourceOptions(
            options.SourceFileMap,
            options.SourceLinkOptions,
            options.SymbolOptions,
            options.RequireExactSource);
        _sourceBreakpoints.SetRuntimeOptions(
            options.SuppressJitOptimizations,
            options.EnableHotReload,
            options.JustMyCode,
            options.EnableStepFiltering);
        return BeginLaunchCoreAsync(cancellationToken);
    }

    private async Task ResetFailedManagedLaunchAsync(bool runtimeAvailable = true)
    {
        if (_debuggee is not null)
        {
            await _debuggee.DisposeAsync().ConfigureAwait(false);
            _debuggee = null;
        }

        await _actor.InvokeAsync(
            token =>
            {
                _sourceBreakpoints.ResetRuntimeBindings(runtimeAvailable);
                _functionBreakpoints.ResetRuntimeBindings(runtimeAvailable);
                _instructionBreakpoints.ResetRuntimeBindings(runtimeAvailable);
                _entryBreakpoint.Reset(runtimeAvailable);
                if (!runtimeAvailable)
                {
                    _state = DebugSessionState.Faulted;
                }

                return ResetFailedLaunchCoreAsync(token);
            },
            CancellationToken.None).ConfigureAwait(false);
    }
}
