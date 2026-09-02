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
    private IDebuggeeProcess? _debuggee;
    private Task? _debuggeeLifetime;
    private DebugStopGeneration _stopGeneration;
    private volatile DebugSessionState _state = DebugSessionState.Created;
    private int _disposed;

    /// <summary>
    /// Creates a debugger session connected to one protocol or control-plane observer.
    /// </summary>
    /// <param name="observer">The ordered target notification observer.</param>
    internal DebuggerSession(IDebuggerSessionObserver observer)
    {
        _observer = observer;
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
            _debuggee = await CorDebugDebuggee.LaunchAsync(options, _actor, cancellationToken)
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

            await _actor.InvokeAsync(ResetFailedLaunchCoreAsync, CancellationToken.None)
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
                await _debuggee.DisposeAsync().ConfigureAwait(false);
                _debuggee = null;
                _debuggeeLifetime = null;
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
        _stopGeneration = _stopGeneration.Value == 0
            ? DebugStopGeneration.First
            : _stopGeneration.Next();
        _state = DebugSessionState.Stopped;
        await _observer.OnStoppedAsync(
            "pause",
            _stopGeneration,
            cancellationToken).ConfigureAwait(false);
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
}
