using Csls.Debugger.Contracts;

namespace Csls.Debugger;

/// <summary>
/// Owns one protocol-neutral debugger target and its ordered lifecycle.
/// </summary>
public sealed class DebuggerSession : IAsyncDisposable
{
    private readonly IDebuggerSessionObserver _observer;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly DebuggerSessionActor _actor = new();
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private readonly SourceBreakpointManager _sourceBreakpoints;
    private IDebuggeeProcess? _debuggee;
    private Task? _debuggeeLifetime;
    private DebugStopGeneration _stopGeneration;
    private volatile DebugSessionState _state = DebugSessionState.Created;
    private int? _pendingStopThreadId;
    private int _disposed;

    /// <summary>
    /// Creates a debugger session connected to one protocol or control-plane observer.
    /// </summary>
    /// <param name="observer">The ordered target notification observer.</param>
    internal DebuggerSession(IDebuggerSessionObserver observer)
    {
        _observer = observer;
        _sourceBreakpoints = new SourceBreakpointManager(
            (breakpoint, cancellationToken) =>
                _observer.OnBreakpointChangedAsync(breakpoint, cancellationToken));
    }

    /// <summary>
    /// Gets the current protocol-neutral debugger state.
    /// </summary>
    public DebugSessionState State => _state;

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
    /// Pauses the managed target at a runtime-consistent inspection point.
    /// </summary>
    /// <param name="cancellationToken">Cancels queueing the pause operation.</param>
    /// <returns>A task that completes after the stopped notification is accepted.</returns>
    public Task PauseAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        return _actor.InvokeAsync(PauseCoreAsync, cancellationToken);
    }

    /// <summary>
    /// Resumes every managed thread from the current debugger stop.
    /// </summary>
    /// <param name="cancellationToken">Cancels queueing the continue operation.</param>
    /// <returns>A task that completes after the continued notification is accepted.</returns>
    public Task ContinueAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        return _actor.InvokeAsync(ContinueCoreAsync, cancellationToken);
    }

    /// <summary>
    /// Performs one source-level step on a managed thread.
    /// </summary>
    /// <param name="threadId">The managed thread identifier to step.</param>
    /// <param name="kind">The requested source-level stepping operation.</param>
    /// <param name="cancellationToken">Cancels queueing the step operation.</param>
    /// <returns>A task that completes after the continued notification is accepted.</returns>
    public Task StepAsync(
        int threadId,
        DebugStepKind kind,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        return _actor.InvokeAsync(
            token => StepCoreAsync(threadId, kind, token),
            cancellationToken);
    }

    /// <summary>
    /// Replaces every source breakpoint requested for one absolute document path.
    /// </summary>
    /// <param name="sourcePath">The absolute source document path.</param>
    /// <param name="breakpoints">The complete replacement breakpoint list.</param>
    /// <param name="cancellationToken">Cancels queueing or runtime binding.</param>
    /// <returns>The ordered current breakpoint binding states.</returns>
    public async Task<IReadOnlyList<DebugSourceBreakpointInfo>> SetSourceBreakpointsAsync(
        string sourcePath,
        IReadOnlyList<DebugSourceBreakpointRequest> breakpoints,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        IReadOnlyList<DebugSourceBreakpointInfo>? result = null;
        await _actor.InvokeAsync(
            async token =>
            {
                if (_state is not DebugSessionState.Created and not DebugSessionState.Stopped)
                {
                    throw new InvalidOperationException(
                        $"Source breakpoints cannot be changed while the debugger session is {_state}.");
                }

                result = await _sourceBreakpoints
                    .SetAsync(sourcePath, breakpoints, token)
                    .ConfigureAwait(false);
            },
            cancellationToken).ConfigureAwait(false);
        return result!;
    }

    /// <summary>
    /// Gets the managed threads belonging to the current stop generation.
    /// </summary>
    /// <param name="cancellationToken">Cancels queueing thread enumeration.</param>
    /// <returns>The bounded current managed-thread snapshot.</returns>
    public async Task<IReadOnlyList<DebugThreadInfo>> GetThreadsAsync(
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        IReadOnlyList<DebugThreadInfo>? result = null;
        await _actor.InvokeAsync(
            token =>
            {
                _ = token;
                if (_state != DebugSessionState.Stopped ||
                    _debuggee is not CorDebugDebuggee managedDebuggee)
                {
                    throw new InvalidOperationException(
                        $"Managed threads are unavailable while the debugger session is {_state}.");
                }

                result = managedDebuggee.GetThreads();
                return ValueTask.CompletedTask;
            },
            cancellationToken).ConfigureAwait(false);
        return result!;
    }

    /// <summary>
    /// Gets a page of managed modules observed in the active target.
    /// </summary>
    /// <param name="startModule">The zero-based first module to return.</param>
    /// <param name="moduleCount">The maximum count, or zero for all remaining modules.</param>
    /// <param name="cancellationToken">Cancels queueing module inspection.</param>
    /// <returns>The selected module page and complete module count.</returns>
    public async Task<DebugModulePage> GetModulesAsync(
        int startModule,
        int moduleCount,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        DebugModulePage? result = null;
        await _actor.InvokeAsync(
            token =>
            {
                _ = token;
                if (_state is not DebugSessionState.Running and not DebugSessionState.Stopped)
                {
                    throw new InvalidOperationException(
                        $"Managed modules are unavailable while the debugger session is {_state}.");
                }

                result = new DebugModulePage(
                    _sourceBreakpoints.GetModules(startModule, moduleCount),
                    _sourceBreakpoints.ModuleCount);
                return ValueTask.CompletedTask;
            },
            cancellationToken).ConfigureAwait(false);
        return result!;
    }

    /// <summary>
    /// Gets a page of managed stack frames belonging to the current stop generation.
    /// </summary>
    /// <param name="threadId">The managed thread identifier.</param>
    /// <param name="startFrame">The zero-based first frame to return.</param>
    /// <param name="levels">The maximum count, or zero for all remaining frames.</param>
    /// <param name="cancellationToken">Cancels queueing stack enumeration.</param>
    /// <returns>The selected frame page and complete stack count.</returns>
    public async Task<DebugStackTrace> GetStackTraceAsync(
        int threadId,
        int startFrame,
        int levels,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        DebugStackTrace? result = null;
        await _actor.InvokeAsync(
            token =>
            {
                _ = token;
                if (_state != DebugSessionState.Stopped ||
                    _debuggee is not CorDebugDebuggee managedDebuggee)
                {
                    throw new InvalidOperationException(
                        $"Managed stack frames are unavailable while the debugger session is {_state}.");
                }

                result = managedDebuggee.GetStackTrace(
                    threadId,
                    _stopGeneration,
                    startFrame,
                    levels);
                return ValueTask.CompletedTask;
            },
            cancellationToken).ConfigureAwait(false);
        return result!;
    }

    /// <summary>
    /// Gets runtime-backed scopes for a frame in the current stop generation.
    /// </summary>
    /// <param name="frameId">The generation-bound managed frame handle.</param>
    /// <param name="cancellationToken">Cancels queueing scope creation.</param>
    /// <returns>The frame's available variable scopes.</returns>
    public async Task<IReadOnlyList<DebugScopeInfo>> GetScopesAsync(
        int frameId,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        IReadOnlyList<DebugScopeInfo>? result = null;
        await _actor.InvokeAsync(
            token =>
            {
                _ = token;
                if (_state != DebugSessionState.Stopped ||
                    _debuggee is not CorDebugDebuggee managedDebuggee)
                {
                    throw new InvalidOperationException(
                        $"Managed scopes are unavailable while the debugger session is {_state}.");
                }

                result = managedDebuggee.GetScopes(frameId, _stopGeneration);
                return ValueTask.CompletedTask;
            },
            cancellationToken).ConfigureAwait(false);
        return result!;
    }

    /// <summary>
    /// Gets one page of immediate variables from a current-generation scope.
    /// </summary>
    /// <param name="variablesReference">The generation-bound variable-container handle.</param>
    /// <param name="start">The zero-based first variable to return.</param>
    /// <param name="count">The maximum count, or zero for all remaining values.</param>
    /// <param name="cancellationToken">Cancels queueing variable enumeration.</param>
    /// <returns>The requested immediate variable page.</returns>
    public async Task<IReadOnlyList<DebugVariableInfo>> GetVariablesAsync(
        int variablesReference,
        int start,
        int count,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        IReadOnlyList<DebugVariableInfo>? result = null;
        await _actor.InvokeAsync(
            token =>
            {
                _ = token;
                if (_state != DebugSessionState.Stopped ||
                    _debuggee is not CorDebugDebuggee managedDebuggee)
                {
                    throw new InvalidOperationException(
                        $"Managed variables are unavailable while the debugger session is {_state}.");
                }

                result = managedDebuggee.GetVariables(
                    variablesReference,
                    _stopGeneration,
                    start,
                    count);
                return ValueTask.CompletedTask;
            },
            cancellationToken).ConfigureAwait(false);
        return result!;
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await _lifecycleGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            await _actor.InvokeAsync(TerminateCoreAsync, CancellationToken.None).ConfigureAwait(false);
            if (_debuggeeLifetime is not null)
            {
                await _debuggeeLifetime.WaitAsync(CancellationToken.None).ConfigureAwait(false);
            }

            if (_debuggee is not null)
            {
                await _actor.InvokeAsync(
                    token =>
                    {
                        _ = token;
                        _sourceBreakpoints.Dispose();
                        return ValueTask.CompletedTask;
                    },
                    CancellationToken.None).ConfigureAwait(false);
                await _debuggee.DisposeAsync().ConfigureAwait(false);
                _debuggee = null;
                _debuggeeLifetime = null;
            }
            else
            {
                _sourceBreakpoints.Dispose();
            }
        }
        finally
        {
            _ = _lifecycleGate.Release();
        }

        await _lifetime.CancelAsync().ConfigureAwait(false);
        await _actor.DisposeAsync().ConfigureAwait(false);
        _lifetime.Dispose();
        _lifecycleGate.Dispose();
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

        _debuggeeLifetime = ObserveDebuggeeAsync(debuggee, _lifetime.Token);
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

    private async ValueTask PauseCoreAsync(CancellationToken cancellationToken)
    {
        if (_state != DebugSessionState.Running ||
            _debuggee is not CorDebugDebuggee managedDebuggee)
        {
            throw new InvalidOperationException(
                $"A managed target cannot be paused while the debugger session is {_state}.");
        }

        managedDebuggee.Pause();
        await EnterStoppedStateAsync("pause", threadId: null, cancellationToken)
            .ConfigureAwait(false);
    }

    private async ValueTask ContinueCoreAsync(CancellationToken cancellationToken)
    {
        if (_state != DebugSessionState.Stopped ||
            _debuggee is not CorDebugDebuggee managedDebuggee)
        {
            throw new InvalidOperationException(
                $"A managed target cannot continue while the debugger session is {_state}.");
        }

        managedDebuggee.Continue();
        _state = DebugSessionState.Running;
        await _observer.OnContinuedAsync(cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask StepCoreAsync(
        int threadId,
        DebugStepKind kind,
        CancellationToken cancellationToken)
    {
        if (_state != DebugSessionState.Stopped ||
            _debuggee is not CorDebugDebuggee managedDebuggee)
        {
            throw new InvalidOperationException(
                $"A managed target cannot step while the debugger session is {_state}.");
        }

        managedDebuggee.Step(threadId, kind);
        _state = DebugSessionState.Running;
        await _observer.OnContinuedAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task ObserveDebuggeeAsync(
        IDebuggeeProcess debuggee,
        CancellationToken cancellationToken)
    {
        Task standardOutput = debuggee.CopyStandardOutputAsync(
            (value, token) => _observer.OnOutputAsync(
                DebugOutputCategory.StandardOutput,
                value,
                token),
            cancellationToken);
        Task standardError = debuggee.CopyStandardErrorAsync(
            (value, token) => _observer.OnOutputAsync(
                DebugOutputCategory.StandardError,
                value,
                token),
            cancellationToken);
        int exitCode = await debuggee.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        await Task.WhenAll(standardOutput, standardError).ConfigureAwait(false);
        await _actor.InvokeAsync(
            async token =>
            {
                await _observer.OnExitedAsync(exitCode, token).ConfigureAwait(false);
                _state = DebugSessionState.Terminated;
                await _observer.OnTerminatedAsync(token).ConfigureAwait(false);
            },
            cancellationToken).ConfigureAwait(false);
    }

    private ValueTask HandleRuntimeBreakpointCoreAsync(
        int threadId,
        CancellationToken cancellationToken)
    {
        if (_state == DebugSessionState.Starting)
        {
            _pendingStopThreadId = threadId;
            return ValueTask.CompletedTask;
        }

        if (_state != DebugSessionState.Running)
        {
            throw new InvalidOperationException(
                $"A runtime breakpoint cannot stop a debugger session while it is {_state}.");
        }

        if (_debuggee is CorDebugDebuggee managedDebuggee)
        {
            managedDebuggee.CancelStep();
        }

        return EnterStoppedStateAsync("breakpoint", threadId, cancellationToken);
    }

    private async ValueTask<bool> HandleRuntimeStepCoreAsync(
        int threadId,
        nint stepper,
        int reason,
        CancellationToken cancellationToken)
    {
        _ = reason;
        if (_state != DebugSessionState.Running ||
            _debuggee is not CorDebugDebuggee managedDebuggee ||
            !managedDebuggee.CompleteStep(stepper))
        {
            return false;
        }

        await EnterStoppedStateAsync("step", threadId, cancellationToken)
            .ConfigureAwait(false);
        return true;
    }

    private async ValueTask EnterStoppedStateAsync(
        string reason,
        int? threadId,
        CancellationToken cancellationToken)
    {
        _stopGeneration = _stopGeneration.Value == 0
            ? DebugStopGeneration.First
            : _stopGeneration.Next();
        _state = DebugSessionState.Stopped;
        await _observer.OnStoppedAsync(
            reason,
            threadId,
            _stopGeneration,
            cancellationToken).ConfigureAwait(false);
    }
}
