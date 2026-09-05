using Csls.Debugger.Contracts;
using Csls.Debugger.Interop;

namespace Csls.Debugger;

/// <summary>
/// Configures and invokes CoreCLR managed source steppers.
/// </summary>
internal sealed partial class CorDebugDebuggee
{
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
        return thread != 0
            ? thread
            : throw new InvalidOperationException(
                $"Managed thread {threadId} no longer exists.");
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

    private unsafe int StartStep(nint stepper, nint thread, DebugStepKind kind)
    {
        var api = new ICorDebugStepperAbi(stepper);
        if (kind == DebugStepKind.Out)
        {
            return api.StepOut();
        }

        if (!ManagedSymbolStepRangeResolver.TryResolve(
            thread,
            _sourceBreakpoints.FindModule,
            out ManagedStepRange range))
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

    private static unsafe int StartGuardedTargetStep(
        nint stepper,
        ManagedStepTargetHandle target)
    {
        uint* nativeRange = stackalloc uint[2];
        nativeRange[0] = target.StartIlOffset;
        nativeRange[1] = target.EndIlOffset;
        return new ICorDebugStepperAbi(stepper).StepRange(
            bStepIn: 0,
            (nint)nativeRange,
            cRangeCount: 1);
    }

    private static void ReleaseUnusedStepper(nint stepper, bool runtimeAvailable)
    {
        if (stepper == 0)
        {
            return;
        }

        if (runtimeAvailable)
        {
            _ = new ICorDebugStepperAbi(stepper).Deactivate();
        }
        _ = ComAbi.Release(stepper);
    }
}
