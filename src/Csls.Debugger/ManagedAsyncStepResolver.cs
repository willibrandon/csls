using Csls.Debugger.Interop;

namespace Csls.Debugger;

/// <summary>
/// Resolves the next compiler-recorded await reachable from an active managed frame.
/// </summary>
internal static class ManagedAsyncStepResolver
{
    /// <summary>
    /// Tries to create an asynchronous step plan for one stopped managed thread.
    /// </summary>
    /// <param name="thread">The borrowed runtime thread pointer.</param>
    /// <param name="moduleResolver">Resolves retained symbol state for a runtime module.</param>
    /// <param name="plan">Receives the next yield and resume locations.</param>
    /// <returns>True when the current state-machine method has a later await.</returns>
    internal static unsafe bool TryResolve(
        nint thread,
        Func<nint, CorDebugLoadedModule?> moduleResolver,
        out ManagedAsyncStepPlan plan)
    {
        ArgumentOutOfRangeException.ThrowIfZero(thread);
        ArgumentNullException.ThrowIfNull(moduleResolver);
        plan = default;
        nint frame = 0;
        nint ilFrame = 0;
        nint function = 0;
        nint module = 0;
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
                return false;
            }

            uint methodToken = 0;
            uint ilOffset = 0;
            int mappingResult = 0;
            uint* methodTokenAddress = &methodToken;
            uint* ilOffsetAddress = &ilOffset;
            int* mappingResultAddress = &mappingResult;
            CorDebugHResult.ThrowIfFailed(
                new ICorDebugFrameAbi(frame).GetFunctionToken((nint)methodTokenAddress),
                "ICorDebugFrame.GetFunctionToken");
            CorDebugHResult.ThrowIfFailed(
                new ICorDebugILFrameAbi(ilFrame).GetIP(
                    (nint)ilOffsetAddress,
                    (nint)mappingResultAddress),
                "ICorDebugILFrame.GetIP");
            methodToken = Volatile.Read(ref *methodTokenAddress);
            ilOffset = Volatile.Read(ref *ilOffsetAddress);

            nint* functionAddress = &function;
            CorDebugHResult.ThrowIfFailed(
                new ICorDebugFrameAbi(frame).GetFunction((nint)functionAddress),
                "ICorDebugFrame.GetFunction");
            function = Volatile.Read(ref *functionAddress);
            nint* moduleAddress = &module;
            CorDebugHResult.ThrowIfFailed(
                new ICorDebugFunctionAbi(function).GetModule((nint)moduleAddress),
                "ICorDebugFunction.GetModule");
            module = Volatile.Read(ref *moduleAddress);
            CorDebugLoadedModule? loadedModule = moduleResolver(module);
            if (loadedModule is null)
            {
                return false;
            }

            using DebugSymbolReader? symbols = loadedModule.OpenSymbols();
            if (symbols is null ||
                !symbols.TryGetNextAsyncAwait(methodToken, ilOffset, out ManagedAsyncAwaitPoint point))
            {
                return false;
            }

            plan = new ManagedAsyncStepPlan(loadedModule.Pointer, methodToken, point);
            return true;
        }
        catch (Exception exception) when (
            DebugSymbolReader.IsReadFailure(exception) ||
            exception is InvalidOperationException or ArgumentException)
        {
            return false;
        }
        finally
        {
            Release(module);
            Release(function);
            Release(ilFrame);
            Release(frame);
        }
    }

    private static void Release(nint pointer)
    {
        if (pointer != 0)
        {
            _ = ComAbi.Release(pointer);
        }
    }
}
