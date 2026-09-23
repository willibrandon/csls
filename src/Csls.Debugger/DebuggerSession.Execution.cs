using Csls.Debugger.Contracts;

namespace Csls.Debugger;

/// <summary>
/// Coordinates protocol-neutral managed execution state changes.
/// </summary>
public sealed partial class DebuggerSession
{
    /// <summary>
    /// Stops a live managed target or preserves its existing stopped state.
    /// </summary>
    /// <param name="cancellationToken">Cancels queueing the pause operation.</param>
    /// <returns>True if this request published a new stop; false if the target was already stopped.</returns>
    public async Task<bool> PauseAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        bool stopped = false;
        await _actor.InvokeAsync(
            async token => stopped = await PauseCoreAsync(token).ConfigureAwait(false),
            cancellationToken).ConfigureAwait(false);
        return stopped;
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
    /// <param name="targetId">The optional generation-bound Step Into call target.</param>
    /// <param name="cancellationToken">Cancels queueing the step operation.</param>
    /// <returns>A task that completes after the continued notification is accepted.</returns>
    public Task StepAsync(
        int threadId,
        DebugStepKind kind,
        int? targetId,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        return _actor.InvokeAsync(
            token => StepCoreAsync(threadId, kind, targetId, token),
            cancellationToken);
    }

    private async ValueTask<bool> PauseCoreAsync(CancellationToken cancellationToken)
    {
        if (_state is not (DebugSessionState.Running or DebugSessionState.Stopped) ||
            _debuggee is not CorDebugDebuggee managedDebuggee)
        {
            throw new InvalidOperationException(
                $"A managed target cannot be paused while the debugger session is {_state}.");
        }

        if (_state == DebugSessionState.Stopped)
        {
            return false;
        }

        managedDebuggee.Pause();
        IReadOnlyList<DebugThreadInfo> threads = managedDebuggee.GetThreads();
        int? threadId = threads.Count == 0 ? null : threads[0].Id;
        await EnterStoppedStateAsync("pause", threadId, cancellationToken)
            .ConfigureAwait(false);
        return true;
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
        int? targetId,
        CancellationToken cancellationToken)
    {
        if (_state != DebugSessionState.Stopped ||
            _debuggee is not CorDebugDebuggee managedDebuggee)
        {
            throw new InvalidOperationException(
                $"A managed target cannot step while the debugger session is {_state}.");
        }

        _sourceBreakpoints.ActivateSteppingPolicy();
        managedDebuggee.Step(threadId, kind, targetId, _stopGeneration);
        _currentException = null;
        _currentExceptionThreadId = null;
        _state = DebugSessionState.Running;
        await _observer.OnContinuedAsync(cancellationToken).ConfigureAwait(false);
    }

}
