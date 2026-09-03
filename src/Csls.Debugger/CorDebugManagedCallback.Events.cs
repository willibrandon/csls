using Csls.Debugger.Interop;

namespace Csls.Debugger;

/// <summary>
/// Handles managed step, module, and exception callbacks.
/// </summary>
internal sealed partial class CorDebugManagedCallback
{
    private ValueTask<bool> HandleStepCompleteAsync(
        nint thread,
        nint stepper,
        int reason,
        CancellationToken cancellationToken)
    {
        if (thread == 0 || stepper == 0)
        {
            return ValueTask.FromResult(true);
        }

        return CompleteStepAsync(thread, stepper, reason, cancellationToken);
    }

    private async ValueTask<bool> CompleteStepAsync(
        nint thread,
        nint stepper,
        int reason,
        CancellationToken cancellationToken)
    {
        bool recognized = await _stepCompleted(
            checked((int)GetThreadId(thread)),
            stepper,
            reason,
            cancellationToken).ConfigureAwait(false);
        return !recognized;
    }

    private async ValueTask<bool> HandleLoadModuleAsync(
        nint module,
        CancellationToken cancellationToken)
    {
        await _sourceBreakpoints.LoadModuleAsync(module, cancellationToken).ConfigureAwait(false);
        await _functionBreakpoints.LoadModuleAsync(module, cancellationToken).ConfigureAwait(false);
        await _instructionBreakpoints.LoadModuleAsync(module, cancellationToken)
            .ConfigureAwait(false);
        return true;
    }

    private async ValueTask<bool> HandleUnloadModuleAsync(
        nint module,
        CancellationToken cancellationToken)
    {
        await _sourceBreakpoints.UnloadModuleAsync(module, cancellationToken)
            .ConfigureAwait(false);
        await _functionBreakpoints.UnloadModuleAsync(module, cancellationToken)
            .ConfigureAwait(false);
        await _instructionBreakpoints.UnloadModuleAsync(module, cancellationToken)
            .ConfigureAwait(false);
        return true;
    }

    private async ValueTask<bool> HandleExceptionAsync(
        nint thread,
        int eventType,
        CancellationToken cancellationToken)
    {
        if (thread == 0 || !TryGetExceptionStage(eventType, out DebugExceptionStage stage))
        {
            return true;
        }

        bool stopped = await _exceptionRaised(
            checked((int)GetThreadId(thread)),
            thread,
            stage,
            cancellationToken).ConfigureAwait(false);
        return !stopped;
    }

    private static bool TryGetExceptionStage(int eventType, out DebugExceptionStage stage)
    {
        stage = eventType switch
        {
            1 => DebugExceptionStage.Thrown,
            2 => DebugExceptionStage.UserUnhandled,
            4 => DebugExceptionStage.Unhandled,
            _ => default
        };
        return eventType is 1 or 2 or 4;
    }

    private static unsafe uint GetThreadId(nint thread)
    {
        uint threadId = 0;
        uint* threadIdAddress = &threadId;
        CorDebugHResult.ThrowIfFailed(
            new ICorDebugThreadAbi(thread).GetID((nint)threadIdAddress),
            "ICorDebugThread.GetID");
        return Volatile.Read(ref *threadIdAddress);
    }
}
