using Csls.Debugger.Contracts;
using Csls.Debugger.Interop;

namespace Csls.Debugger;

/// <summary>
/// Owns one active CoreCLR source step and its Just My Code policy.
/// </summary>
internal sealed partial class CorDebugDebuggee
{
    /// <summary>
    /// Starts one source-level step on a managed thread and resumes the target.
    /// </summary>
    /// <param name="threadId">The managed thread identifier to step.</param>
    /// <param name="kind">The requested source-level stepping operation.</param>
    internal void Step(int threadId, DebugStepKind kind)
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
            thread = GetThread(threadId);
            stepper = CreateStepper(thread);
            ConfigureStepper(stepper);
            int stepResult = StartStep(stepper, thread, kind);
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
            ReleaseUnusedStepper(stepper);
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

    private unsafe nint GetThread(int threadId)
    {
        nint thread = 0;
        nint* threadAddress = &thread;
        CorDebugHResult.ThrowIfFailed(
            new ICorDebugProcessAbi(_debugProcess).GetThread(
                checked((uint)threadId),
                (nint)threadAddress),
            "ICorDebugProcess.GetThread");
        thread = Volatile.Read(ref *threadAddress);
        if (thread == 0)
        {
            throw new InvalidOperationException($"Managed thread {threadId} no longer exists.");
        }

        return thread;
    }

    private static unsafe nint CreateStepper(nint thread)
    {
        nint stepper = 0;
        nint* stepperAddress = &stepper;
        CorDebugHResult.ThrowIfFailed(
            new ICorDebugThreadAbi(thread).CreateStepper((nint)stepperAddress),
            "ICorDebugThread.CreateStepper");
        stepper = Volatile.Read(ref *stepperAddress);
        return stepper != 0
            ? stepper
            : throw new InvalidOperationException(
                "ICorDebugThread.CreateStepper returned no stepper.");
    }

    private static void ConfigureStepper(nint stepper)
    {
        var api = new ICorDebugStepperAbi(stepper);
        CorDebugHResult.ThrowIfFailed(
            api.SetInterceptMask(mask: 0),
            "ICorDebugStepper.SetInterceptMask");
        CorDebugHResult.ThrowIfFailed(
            api.SetUnmappedStopMask(mask: 0),
            "ICorDebugStepper.SetUnmappedStopMask");
        CorDebugHResult.ThrowIfFailed(api.SetRangeIL(bIL: 1), "ICorDebugStepper.SetRangeIL");
        nint stepper2 = ComAbi.QueryInterface(stepper, ICorDebugStepper2Abi.InterfaceId);
        try
        {
            CorDebugHResult.ThrowIfFailed(
                new ICorDebugStepper2Abi(stepper2).SetJMC(fIsJMCStepper: 1),
                "ICorDebugStepper2.SetJMC");
        }
        finally
        {
            _ = ComAbi.Release(stepper2);
        }
    }

    private static unsafe int StartStep(nint stepper, nint thread, DebugStepKind kind)
    {
        var api = new ICorDebugStepperAbi(stepper);
        if (kind == DebugStepKind.Out)
        {
            return api.StepOut();
        }

        if (!PortablePdbStepRangeResolver.TryResolve(thread, out ManagedStepRange range))
        {
            return api.Step(kind == DebugStepKind.Into ? 1 : 0);
        }

        uint* nativeRange = stackalloc uint[2];
        nativeRange[0] = range.StartOffset;
        nativeRange[1] = range.EndOffset;
        return api.StepRange(
            kind == DebugStepKind.Into ? 1 : 0,
            (nint)nativeRange,
            cRangeCount: 1);
    }

    private static void ReleaseUnusedStepper(nint stepper)
    {
        if (stepper == 0)
        {
            return;
        }

        _ = new ICorDebugStepperAbi(stepper).Deactivate();
        _ = ComAbi.Release(stepper);
    }
}
