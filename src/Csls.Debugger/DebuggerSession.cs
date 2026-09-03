using Csls.Debugger.Contracts;

namespace Csls.Debugger;

/// <summary>
/// Owns one protocol-neutral debugger target and its ordered lifecycle.
/// </summary>
public sealed partial class DebuggerSession : IAsyncDisposable
{
    private readonly IDebuggerSessionObserver _observer;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly DebuggerSessionActor _actor = new();
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private readonly SourceBreakpointManager _sourceBreakpoints;
    private IDebuggeeProcess? _debuggee;
    private Task? _debuggeeLifetime;
    private CancellationTokenSource? _debuggeeObservationCancellation;
    private DebugStopGeneration _stopGeneration;
    private volatile DebugSessionState _state = DebugSessionState.Created;
    private readonly HashSet<DebugExceptionBreakMode> _exceptionBreakModes =
        [DebugExceptionBreakMode.Unhandled];
    private PendingDebugStop? _pendingStop;
    private DebugExceptionInfo? _currentException;
    private int? _currentExceptionThreadId;
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
            if (_debuggee is not null &&
                _state is not DebugSessionState.Terminated and not DebugSessionState.Faulted)
            {
                if (_debuggee.OwnsProcess)
                {
                    await _actor.InvokeAsync(TerminateCoreAsync, CancellationToken.None)
                        .ConfigureAwait(false);
                    if (_debuggeeLifetime is not null)
                    {
                        await _debuggeeLifetime.WaitAsync(CancellationToken.None)
                            .ConfigureAwait(false);
                    }
                }
                else
                {
                    await _actor.InvokeAsync(
                        token =>
                        {
                            _ = token;
                            _state = DebugSessionState.Terminating;
                            return ValueTask.CompletedTask;
                        },
                        CancellationToken.None).ConfigureAwait(false);
                    await _debuggee.DetachAsync(CancellationToken.None).ConfigureAwait(false);
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
        _debuggeeObservationCancellation?.Dispose();
        await _actor.DisposeAsync().ConfigureAwait(false);
        _lifetime.Dispose();
        _lifecycleGate.Dispose();
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
        _currentException = null;
        _currentExceptionThreadId = null;
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
        _currentException = null;
        _currentExceptionThreadId = null;
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
            _pendingStop = new PendingDebugStop("breakpoint", threadId, Exception: null);
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
        if (!string.Equals(reason, "exception", StringComparison.Ordinal))
        {
            _currentException = null;
            _currentExceptionThreadId = null;
        }

        _stopGeneration = _stopGeneration.Value == 0
            ? DebugStopGeneration.First
            : _stopGeneration.Next();
        _state = DebugSessionState.Stopped;
        await _observer.OnStoppedAsync(
            reason,
            threadId,
            _stopGeneration,
            _currentException,
            cancellationToken).ConfigureAwait(false);
    }
}
