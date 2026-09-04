using Csls.Debugger.Interop;

namespace Csls.Debugger;

/// <summary>
/// Owns the temporary callee breakpoint used by source-aware Step Into.
/// </summary>
internal sealed partial class CorDebugDebuggee
{
    /// <summary>
    /// Classifies and completes one temporary callee-breakpoint callback.
    /// </summary>
    /// <param name="threadId">The callback's managed thread identifier.</param>
    /// <param name="breakpoint">The borrowed runtime breakpoint pointer.</param>
    /// <returns>The required callback continuation behavior.</returns>
    internal ManagedTargetBreakpointDecision CompleteTargetBreakpoint(
        int threadId,
        nint breakpoint)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(threadId);
        ArgumentOutOfRangeException.ThrowIfZero(breakpoint);
        ManagedTargetBreakpointDecision asyncDecision = CompleteAsyncBreakpoint(
            threadId,
            breakpoint);
        if (asyncDecision != ManagedTargetBreakpointDecision.Unrecognized)
        {
            return asyncDecision;
        }

        ManagedTargetBreakpoint? target = _targetBreakpoint;
        if (target is null || target.ThreadId != threadId)
        {
            return ManagedTargetBreakpointDecision.Unrecognized;
        }

        nint identity = ComAbi.GetIdentity(breakpoint);
        try
        {
            if (identity != target.Identity)
            {
                return ManagedTargetBreakpointDecision.Unrecognized;
            }

            if (target.HitsToSkip > 0)
            {
                target.HitsToSkip--;
                return ManagedTargetBreakpointDecision.Continue;
            }

            ReleaseTargetBreakpoint();
            ReleaseActiveStepper(deactivate: true);
            ClearFrameHandles();
            return ManagedTargetBreakpointDecision.Stopped;
        }
        finally
        {
            _ = ComAbi.Release(identity);
        }
    }

    private unsafe void CreateTargetBreakpoint(ManagedStepTargetHandle target)
    {
        ManagedFrameHandle frame = _frames.Values.Single(
            candidate => candidate.Id == target.FrameId);
        nint caller = 0;
        nint module = 0;
        nint callee = 0;
        nint code = 0;
        nint breakpoint = 0;
        nint identity = 0;
        try
        {
            caller = GetFrameFunction(frame.Pointer);
            module = GetFunctionModule(caller);
            nint* calleeAddress = &callee;
            CorDebugHResult.ThrowIfFailed(
                new ICorDebugModuleAbi(module).GetFunctionFromToken(
                    target.CalleeMethodToken,
                    (nint)calleeAddress),
                "ICorDebugModule.GetFunctionFromToken");
            callee = Volatile.Read(ref *calleeAddress);
            code = GetFunctionIlCode(callee);
            nint* breakpointAddress = &breakpoint;
            CorDebugHResult.ThrowIfFailed(
                new ICorDebugCodeAbi(code).CreateBreakpoint(
                    target.CalleeEntryIlOffset,
                    (nint)breakpointAddress),
                "ICorDebugCode.CreateBreakpoint");
            breakpoint = Volatile.Read(ref *breakpointAddress);
            CorDebugHResult.ThrowIfFailed(
                new ICorDebugBreakpointAbi(breakpoint).Activate(bActive: 1),
                "ICorDebugBreakpoint.Activate");
            identity = ComAbi.GetIdentity(breakpoint);
            _targetBreakpoint = new ManagedTargetBreakpoint
            {
                Pointer = breakpoint,
                Identity = identity,
                ThreadId = target.ThreadId,
                HitsToSkip = target.HitsToSkip
            };
            breakpoint = 0;
            identity = 0;
        }
        finally
        {
            ReleaseUnclaimedBreakpoint(breakpoint, identity);
            ReleaseCom(code);
            ReleaseCom(callee);
            ReleaseCom(module);
            ReleaseCom(caller);
        }
    }

    private static unsafe nint GetFrameFunction(nint frame)
    {
        nint function = 0;
        nint* address = &function;
        CorDebugHResult.ThrowIfFailed(
            new ICorDebugFrameAbi(frame).GetFunction((nint)address),
            "ICorDebugFrame.GetFunction");
        return Volatile.Read(ref *address);
    }

    private static unsafe nint GetFunctionModule(nint function)
    {
        nint module = 0;
        nint* address = &module;
        CorDebugHResult.ThrowIfFailed(
            new ICorDebugFunctionAbi(function).GetModule((nint)address),
            "ICorDebugFunction.GetModule");
        return Volatile.Read(ref *address);
    }

    private static unsafe nint GetFunctionIlCode(nint function)
    {
        nint code = 0;
        nint* address = &code;
        CorDebugHResult.ThrowIfFailed(
            new ICorDebugFunctionAbi(function).GetILCode((nint)address),
            "ICorDebugFunction.GetILCode");
        return Volatile.Read(ref *address);
    }

    private void ReleaseTargetBreakpoint(bool runtimeAvailable = true)
    {
        ManagedTargetBreakpoint? target = Interlocked.Exchange(ref _targetBreakpoint, null);
        if (target is null)
        {
            return;
        }

        if (runtimeAvailable)
        {
            _ = new ICorDebugBreakpointAbi(target.Pointer).Activate(bActive: 0);
        }

        _ = ComAbi.Release(target.Pointer);
        _ = ComAbi.Release(target.Identity);
    }

    private static void ReleaseUnclaimedBreakpoint(nint breakpoint, nint identity)
    {
        if (breakpoint != 0)
        {
            _ = new ICorDebugBreakpointAbi(breakpoint).Activate(bActive: 0);
            _ = ComAbi.Release(breakpoint);
        }

        ReleaseCom(identity);
    }

    private static void ReleaseCom(nint pointer)
    {
        if (pointer != 0)
        {
            _ = ComAbi.Release(pointer);
        }
    }
}
