using Csls.Debugger.Contracts;
using Csls.Debugger.Interop;

namespace Csls.Debugger;

/// <summary>
/// Controls managed execution and source-level stepping.
/// </summary>
internal sealed partial class CorDebugDebuggee
{
    /// <summary>
    /// Stops all managed threads at a runtime-consistent inspection point.
    /// </summary>
    internal void Pause()
    {
        ClearFrameHandles();
        CorDebugHResult.ThrowIfFailed(
            new ICorDebugControllerAbi(_debugProcess).Stop(dwTimeoutIgnored: 0),
            "ICorDebugController.Stop");
    }

    /// <summary>
    /// Resumes all managed threads from the current debugger stop.
    /// </summary>
    internal void Continue()
    {
        ClearFrameHandles();
        CorDebugHResult.ThrowIfFailed(
            new ICorDebugControllerAbi(_debugProcess).Continue(fIsOutOfBand: 0),
            "ICorDebugController.Continue");
    }

    /// <summary>
    /// Starts one source-level step on a managed thread and resumes the target.
    /// </summary>
    /// <param name="threadId">The managed thread identifier to step.</param>
    /// <param name="kind">The requested source-level stepping operation.</param>
    internal unsafe void Step(int threadId, DebugStepKind kind)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(threadId);
        if (_activeStepper != 0)
        {
            throw new InvalidOperationException("A managed step is already active.");
        }

        nint thread = 0;
        nint stepper = 0;
        try
        {
            nint* threadAddress = &thread;
            CorDebugHResult.ThrowIfFailed(
                new ICorDebugProcessAbi(_debugProcess).GetThread(
                    checked((uint)threadId),
                    (nint)threadAddress),
                "ICorDebugProcess.GetThread");
            thread = Volatile.Read(ref *threadAddress);
            if (thread == 0)
            {
                throw new InvalidOperationException(
                    $"Managed thread {threadId} no longer exists.");
            }

            nint* stepperAddress = &stepper;
            CorDebugHResult.ThrowIfFailed(
                new ICorDebugThreadAbi(thread).CreateStepper((nint)stepperAddress),
                "ICorDebugThread.CreateStepper");
            stepper = Volatile.Read(ref *stepperAddress);
            if (stepper == 0)
            {
                throw new InvalidOperationException(
                    "ICorDebugThread.CreateStepper returned no stepper.");
            }

            var api = new ICorDebugStepperAbi(stepper);
            CorDebugHResult.ThrowIfFailed(
                api.SetInterceptMask(mask: 0),
                "ICorDebugStepper.SetInterceptMask");
            CorDebugHResult.ThrowIfFailed(
                api.SetUnmappedStopMask(mask: 0),
                "ICorDebugStepper.SetUnmappedStopMask");
            CorDebugHResult.ThrowIfFailed(
                api.SetRangeIL(bIL: 1),
                "ICorDebugStepper.SetRangeIL");
            int stepResult;
            if (kind == DebugStepKind.Out)
            {
                stepResult = api.StepOut();
            }
            else if (PortablePdbStepRangeResolver.TryResolve(thread, out ManagedStepRange range))
            {
                uint* nativeRange = stackalloc uint[2];
                nativeRange[0] = range.StartOffset;
                nativeRange[1] = range.EndOffset;
                stepResult = api.StepRange(
                    kind == DebugStepKind.Into ? 1 : 0,
                    (nint)nativeRange,
                    cRangeCount: 1);
            }
            else
            {
                stepResult = api.Step(kind == DebugStepKind.Into ? 1 : 0);
            }

            CorDebugHResult.ThrowIfFailed(stepResult, $"ICorDebugStepper.Step{kind}");
            _activeStepperIdentity = ComAbi.GetIdentity(stepper);
            _activeStepper = stepper;
            stepper = 0;
            ClearFrameHandles();
            CorDebugHResult.ThrowIfFailed(
                new ICorDebugControllerAbi(_debugProcess).Continue(fIsOutOfBand: 0),
                "ICorDebugController.Continue");
        }
        catch
        {
            CancelStep();
            throw;
        }
        finally
        {
            if (stepper != 0)
            {
                _ = new ICorDebugStepperAbi(stepper).Deactivate();
                _ = ComAbi.Release(stepper);
            }

            if (thread != 0)
            {
                _ = ComAbi.Release(thread);
            }
        }
    }

    /// <summary>
    /// Completes the active source step when its runtime callback arrives.
    /// </summary>
    /// <param name="stepper">The borrowed callback ICorDebugStepper pointer.</param>
    /// <returns>True when the callback belongs to this debuggee's active step.</returns>
    internal bool CompleteStep(nint stepper)
    {
        ArgumentOutOfRangeException.ThrowIfZero(stepper);
        nint identity = ComAbi.GetIdentity(stepper);
        try
        {
            if (identity != _activeStepperIdentity)
            {
                return false;
            }

            ReleaseActiveStepper(deactivate: false);
            return true;
        }
        finally
        {
            _ = ComAbi.Release(identity);
        }
    }

    /// <summary>
    /// Deactivates and releases an interrupted source step.
    /// </summary>
    internal void CancelStep() => ReleaseActiveStepper(deactivate: true);
}
