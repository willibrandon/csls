using Csls.Debugger.Contracts;

namespace Csls.Debugger;

/// <summary>
/// Coordinates protocol-neutral managed execution state changes.
/// </summary>
public sealed partial class DebuggerSession
{
    private ValueTask HandleRuntimeBreakpointCoreAsync(
        int threadId,
        DebugBreakpointKind kind,
        CancellationToken cancellationToken)
    {
        string reason = kind switch
        {
            DebugBreakpointKind.Function => "function breakpoint",
            DebugBreakpointKind.Instruction => "instruction breakpoint",
            _ => "breakpoint"
        };
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
            _debuggee is not CorDebugDebuggee managedDebuggee)
        {
            return false;
        }

        if (!managedDebuggee.CompleteStep(stepper))
        {
            return false;
        }

        await EnterStoppedStateAsync("step", threadId, cancellationToken)
            .ConfigureAwait(false);
        return true;
    }

    private async ValueTask<ManagedTargetBreakpointDecision>
        HandleRuntimeTargetBreakpointCoreAsync(
            int threadId,
            nint breakpoint,
            CancellationToken cancellationToken)
    {
        if (_state != DebugSessionState.Running ||
            _debuggee is not CorDebugDebuggee managedDebuggee)
        {
            return ManagedTargetBreakpointDecision.Unrecognized;
        }

        ManagedTargetBreakpointDecision decision = managedDebuggee
            .CompleteTargetBreakpoint(threadId, breakpoint);
        if (decision == ManagedTargetBreakpointDecision.Stopped)
        {
            await EnterStoppedStateAsync("step", threadId, cancellationToken)
                .ConfigureAwait(false);
        }

        return decision;
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
