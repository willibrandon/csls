using Csls.Debugger.Contracts;
using Csls.Debugger.Interop;

namespace Csls.Debugger;

/// <summary>
/// Follows compiler-recorded async continuations across managed runtime threads.
/// </summary>
internal sealed partial class CorDebugDebuggee
{
    private void PrepareAsyncStep(int threadId, nint thread, DebugStepKind kind)
    {
        if (!ManagedAsyncStepResolver.TryResolve(
            thread,
            _sourceBreakpoints.FindModule,
            out ManagedAsyncStepPlan plan))
        {
            return;
        }

        nint stateMachineHandle = TryCreateStateMachineHandle(thread);
        _ = ComAbi.AddRef(plan.Module);
        try
        {
            (nint breakpoint, nint identity) = CreateAsyncBreakpoint(
                plan.Module,
                plan.MethodToken,
                plan.AwaitPoint.YieldOffset);
            _asyncStep = new ManagedAsyncStep
            {
                Breakpoint = breakpoint,
                Identity = identity,
                Module = plan.Module,
                StateMachineHandle = stateMachineHandle,
                InitialThreadId = threadId,
                Kind = kind,
                ResumeMethodToken = plan.AwaitPoint.ResumeMethodToken,
                ResumeOffset = plan.AwaitPoint.ResumeStopOffset
            };
        }
        catch
        {
            ReleaseStateMachineHandle(stateMachineHandle);
            _ = ComAbi.Release(plan.Module);
            throw;
        }
    }

    private ManagedTargetBreakpointDecision CompleteAsyncBreakpoint(
        int threadId,
        nint breakpoint)
    {
        ManagedAsyncStep? step = _asyncStep;
        if (step is null)
        {
            return ManagedTargetBreakpointDecision.Unrecognized;
        }

        nint identity = ComAbi.GetIdentity(breakpoint);
        try
        {
            if (identity != step.Identity)
            {
                return ManagedTargetBreakpointDecision.Unrecognized;
            }

            if (!step.WaitsForResume)
            {
                if (threadId != step.InitialThreadId)
                {
                    return ManagedTargetBreakpointDecision.Continue;
                }

                ReleaseActiveStepper(deactivate: true);
                try
                {
                    ReplaceWithAsyncResumeBreakpoint(step);
                }
                catch
                {
                    ReleaseAsyncStep();
                    throw;
                }

                return ManagedTargetBreakpointDecision.Continue;
            }

            if (step.StateMachineHandle != 0 &&
                !StateMachineMatches(threadId, step.StateMachineHandle))
            {
                return ManagedTargetBreakpointDecision.Continue;
            }

            ReleaseAsyncStep();
            ClearFrameHandles();
            return ManagedTargetBreakpointDecision.Stopped;
        }
        finally
        {
            _ = ComAbi.Release(identity);
        }
    }

    private static unsafe (nint Breakpoint, nint Identity) CreateAsyncBreakpoint(
        nint module,
        uint methodToken,
        uint ilOffset)
    {
        nint function = 0;
        nint code = 0;
        nint breakpoint = 0;
        nint identity = 0;
        try
        {
            nint* functionAddress = &function;
            CorDebugHResult.ThrowIfFailed(
                new ICorDebugModuleAbi(module).GetFunctionFromToken(
                    methodToken,
                    (nint)functionAddress),
                "ICorDebugModule.GetFunctionFromToken");
            function = Volatile.Read(ref *functionAddress);
            nint* codeAddress = &code;
            CorDebugHResult.ThrowIfFailed(
                new ICorDebugFunctionAbi(function).GetILCode((nint)codeAddress),
                "ICorDebugFunction.GetILCode");
            code = Volatile.Read(ref *codeAddress);
            nint* breakpointAddress = &breakpoint;
            CorDebugHResult.ThrowIfFailed(
                new ICorDebugCodeAbi(code).CreateBreakpoint(ilOffset, (nint)breakpointAddress),
                "ICorDebugCode.CreateBreakpoint");
            breakpoint = Volatile.Read(ref *breakpointAddress);
            CorDebugHResult.ThrowIfFailed(
                new ICorDebugBreakpointAbi(breakpoint).Activate(bActive: 1),
                "ICorDebugBreakpoint.Activate");
            identity = ComAbi.GetIdentity(breakpoint);
            nint resultBreakpoint = breakpoint;
            nint resultIdentity = identity;
            breakpoint = 0;
            identity = 0;
            return (resultBreakpoint, resultIdentity);
        }
        finally
        {
            ReleaseCom(identity);
            if (breakpoint != 0)
            {
                _ = new ICorDebugBreakpointAbi(breakpoint).Activate(bActive: 0);
            }

            ReleaseCom(breakpoint);
            ReleaseCom(code);
            ReleaseCom(function);
        }
    }

    private static void ReplaceWithAsyncResumeBreakpoint(ManagedAsyncStep step)
    {
        (nint breakpoint, nint identity) = CreateAsyncBreakpoint(
            step.Module,
            step.ResumeMethodToken,
            step.ResumeOffset);
        ReleaseAsyncBreakpoint(step);
        step.Breakpoint = breakpoint;
        step.Identity = identity;
        step.WaitsForResume = true;
    }

    private void ReleaseAsyncStep(bool runtimeAvailable = true)
    {
        ManagedAsyncStep? step = Interlocked.Exchange(ref _asyncStep, null);
        if (step is null)
        {
            return;
        }

        ReleaseAsyncBreakpoint(step, runtimeAvailable);
        ReleaseStateMachineHandle(step.StateMachineHandle, runtimeAvailable);
        _ = ComAbi.Release(step.Module);
    }

    private static void ReleaseAsyncBreakpoint(ManagedAsyncStep step, bool runtimeAvailable = true)
    {
        if (runtimeAvailable)
        {
            _ = new ICorDebugBreakpointAbi(step.Breakpoint).Activate(bActive: 0);
        }

        _ = ComAbi.Release(step.Identity);
        _ = ComAbi.Release(step.Breakpoint);
    }

    private static unsafe nint TryCreateStateMachineHandle(nint thread)
    {
        nint value = GetFirstArgument(thread);
        nint dereferenced = 0;
        nint heapValue = 0;
        try
        {
            if (value == 0 ||
                !TryDereferenceValue(value, out dereferenced) ||
                !ComAbi.TryQueryInterface(
                    dereferenced,
                    ICorDebugHeapValue2Abi.InterfaceId,
                    out heapValue))
            {
                return 0;
            }

            nint handle = 0;
            nint* handleAddress = &handle;
            int result = new ICorDebugHeapValue2Abi(heapValue).CreateHandle(
                type: 1,
                (nint)handleAddress);
            handle = Volatile.Read(ref *handleAddress);
            if (result >= 0)
            {
                return handle;
            }

            ReleaseStateMachineHandle(handle);
            return 0;
        }
        catch (InvalidOperationException)
        {
            return 0;
        }
        finally
        {
            ReleaseCom(heapValue);
            ReleaseCom(dereferenced);
            ReleaseCom(value);
        }
    }

    private static unsafe nint GetFirstArgument(nint thread)
    {
        nint frame = 0;
        nint ilFrame = 0;
        nint values = 0;
        try
        {
            nint* frameAddress = &frame;
            CorDebugHResult.ThrowIfFailed(
                new ICorDebugThreadAbi(thread).GetActiveFrame((nint)frameAddress),
                "ICorDebugThread.GetActiveFrame");
            frame = Volatile.Read(ref *frameAddress);
            if (frame == 0 ||
                !ComAbi.TryQueryInterface(frame, ICorDebugILFrameAbi.InterfaceId, out ilFrame))
            {
                return 0;
            }

            nint* valuesAddress = &values;
            CorDebugHResult.ThrowIfFailed(
                new ICorDebugILFrameAbi(ilFrame).EnumerateArguments((nint)valuesAddress),
                "ICorDebugILFrame.EnumerateArguments");
            values = Volatile.Read(ref *valuesAddress);
            if (values == 0)
            {
                return 0;
            }

            nint value = 0;
            uint fetched = 0;
            nint* valueAddress = &value;
            uint* fetchedAddress = &fetched;
            CorDebugHResult.ThrowIfFailed(
                new ICorDebugValueEnumAbi(values).Next(
                    1,
                    (nint)valueAddress,
                    (nint)fetchedAddress),
                "ICorDebugValueEnum.Next");
            value = Volatile.Read(ref *valueAddress);
            return Volatile.Read(ref *fetchedAddress) == 1 ? value : 0;
        }
        finally
        {
            ReleaseCom(values);
            ReleaseCom(ilFrame);
            ReleaseCom(frame);
        }
    }

    private bool StateMachineMatches(int threadId, nint stateMachineHandle)
    {
        nint thread = 0;
        nint current = 0;
        try
        {
            try
            {
                thread = GetThread(threadId);
                current = GetFirstArgument(thread);
                return current != 0 &&
                    TryGetReferenceAddress(current, out ulong currentAddress) &&
                    TryGetReferenceAddress(stateMachineHandle, out ulong selectedAddress) &&
                    currentAddress == selectedAddress;
            }
            catch (InvalidOperationException)
            {
                return false;
            }
        }
        finally
        {
            ReleaseCom(current);
            ReleaseCom(thread);
        }
    }

    private static unsafe bool TryGetReferenceAddress(nint value, out ulong address)
    {
        address = 0;
        if (!ComAbi.TryQueryInterface(
            value,
            ICorDebugReferenceValueAbi.InterfaceId,
            out nint reference))
        {
            return false;
        }

        try
        {
            ulong rawAddress = 0;
            ulong* addressPointer = &rawAddress;
            int result = new ICorDebugReferenceValueAbi(reference).GetValue(
                (nint)addressPointer);
            address = Volatile.Read(ref *addressPointer);
            return result >= 0 && address != 0;
        }
        finally
        {
            _ = ComAbi.Release(reference);
        }
    }

    private static void ReleaseStateMachineHandle(nint handle, bool runtimeAvailable = true)
    {
        if (handle == 0)
        {
            return;
        }

        if (runtimeAvailable)
        {
            _ = new ICorDebugHandleValueAbi(handle).Dispose();
        }

        _ = ComAbi.Release(handle);
    }
}
