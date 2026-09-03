using Csls.Debugger.Contracts;

namespace Csls.Debugger;

/// <summary>
/// Coordinates protocol-neutral managed execution state changes.
/// </summary>
public sealed partial class DebuggerSession
{
    private async ValueTask<bool> HandleRuntimeBreakpointCoreAsync(
        int threadId,
        ManagedBreakpointHit hit,
        CancellationToken cancellationToken)
    {
        if (_state == DebugSessionState.Starting)
        {
            if (hit.Definition.Condition is null && hit.Definition.LogMessage is null)
            {
                if (!hit.Definition.RegisterHit())
                {
                    return true;
                }

                _pendingStop = new PendingDebugStop(
                    GetBreakpointReason(hit.Kind),
                    threadId,
                    Exception: null);
            }
            else
            {
                _pendingStop = new PendingDebugStop(
                    GetBreakpointReason(hit.Kind),
                    threadId,
                    Exception: null,
                    hit);
            }

            return false;
        }

        if (_state != DebugSessionState.Running)
        {
            return false;
        }

        return await HandleRunningBreakpointAsync(threadId, hit, cancellationToken)
            .ConfigureAwait(false);
    }

    private async ValueTask StopAtBreakpointAsync(
        int threadId,
        DebugBreakpointKind kind,
        DebugStopGeneration? generation,
        CancellationToken cancellationToken)
    {
        if (_debuggee is CorDebugDebuggee managedDebuggee)
        {
            managedDebuggee.CancelStep();
        }

        await EnterStoppedStateAsync(
            GetBreakpointReason(kind),
            threadId,
            cancellationToken,
            generation).ConfigureAwait(false);
    }

    private static string GetBreakpointReason(DebugBreakpointKind kind) => kind switch
    {
        DebugBreakpointKind.Function => "function breakpoint",
        DebugBreakpointKind.Instruction => "instruction breakpoint",
        _ => "breakpoint"
    };

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
        CancellationToken cancellationToken,
        DebugStopGeneration? generation = null)
    {
        if (!string.Equals(reason, "exception", StringComparison.Ordinal))
        {
            _currentException = null;
            _currentExceptionThreadId = null;
        }

        _stopGeneration = generation ?? (_stopGeneration.Value == 0
            ? DebugStopGeneration.First
            : _stopGeneration.Next());
        _state = DebugSessionState.Stopped;
        await _observer.OnStoppedAsync(
            reason,
            threadId,
            _stopGeneration,
            _currentException,
            cancellationToken).ConfigureAwait(false);
    }
}
