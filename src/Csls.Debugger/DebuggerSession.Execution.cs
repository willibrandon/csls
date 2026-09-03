using Csls.Debugger.Contracts;

namespace Csls.Debugger;

/// <summary>
/// Coordinates protocol-neutral managed execution state changes.
/// </summary>
public sealed partial class DebuggerSession
{
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

        managedDebuggee.Step(
            threadId,
            kind,
            _sourceBreakpoints.ActivateSteppingPolicy());
        _currentException = null;
        _currentExceptionThreadId = null;
        _state = DebugSessionState.Running;
        await _observer.OnContinuedAsync(cancellationToken).ConfigureAwait(false);
    }

    private ValueTask HandleRuntimeBreakpointCoreAsync(
        int threadId,
        DebugBreakpointKind kind,
        CancellationToken cancellationToken)
    {
        string reason = kind == DebugBreakpointKind.Function
            ? "function breakpoint"
            : "breakpoint";
        if (_state == DebugSessionState.Starting)
        {
            _pendingStop = new PendingDebugStop(reason, threadId, Exception: null);
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

        return EnterStoppedStateAsync(reason, threadId, cancellationToken);
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
