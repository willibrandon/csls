using Csls.Debugger.Interop;

namespace Csls.Debugger;

/// <summary>
/// Resolves the current source statement to a half-open managed-symbol IL range.
/// </summary>
internal static class ManagedSymbolStepRangeResolver
{
    /// <summary>
    /// Tries to resolve the active managed frame's current source statement.
    /// </summary>
    /// <param name="thread">The borrowed ICorDebugThread pointer.</param>
    /// <param name="moduleResolver">Resolves the retained symbol state for a runtime module.</param>
    /// <param name="range">Receives the resolved half-open IL range.</param>
    /// <returns>True when managed symbol data describes the current instruction.</returns>
    internal static unsafe bool TryResolve(
        nint thread,
        Func<nint, CorDebugLoadedModule?> moduleResolver,
        out ManagedStepRange range)
    {
        ArgumentOutOfRangeException.ThrowIfZero(thread);
        ArgumentNullException.ThrowIfNull(moduleResolver);
        range = default;
        nint frame = 0;
        nint ilFrame = 0;
        nint function = 0;
        nint module = 0;
        nint code = 0;
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
            nint* codeAddress = &code;
            CorDebugHResult.ThrowIfFailed(
                new ICorDebugFunctionAbi(function).GetILCode((nint)codeAddress),
                "ICorDebugFunction.GetILCode");
            code = Volatile.Read(ref *codeAddress);
            uint codeSize = 0;
            uint* codeSizeAddress = &codeSize;
            CorDebugHResult.ThrowIfFailed(
                new ICorDebugCodeAbi(code).GetSize((nint)codeSizeAddress),
                "ICorDebugCode.GetSize");
            codeSize = Volatile.Read(ref *codeSizeAddress);
            CorDebugLoadedModule? loadedModule = moduleResolver(module);
            return loadedModule is not null && TryResolveSymbols(
                loadedModule,
                methodToken,
                ilOffset,
                codeSize,
                out range);
        }
        catch (Exception exception) when (
            DebugSymbolReader.IsReadFailure(exception) ||
            exception is InvalidOperationException or ArgumentException)
        {
            return false;
        }
        finally
        {
            if (code != 0)
            {
                _ = ComAbi.Release(code);
            }

            if (module != 0)
            {
                _ = ComAbi.Release(module);
            }

            if (function != 0)
            {
                _ = ComAbi.Release(function);
            }

            if (ilFrame != 0)
            {
                _ = ComAbi.Release(ilFrame);
            }

            if (frame != 0)
            {
                _ = ComAbi.Release(frame);
            }
        }
    }

    private static bool TryResolveSymbols(
        CorDebugLoadedModule module,
        uint methodToken,
        uint ilOffset,
        uint codeSize,
        out ManagedStepRange range)
    {
        range = default;
        int rowNumber = checked((int)(methodToken & 0x00ffffff));
        if (rowNumber == 0)
        {
            return false;
        }

        using DebugSymbolReader? symbols = module.OpenSymbols();
        if (symbols is null)
        {
            return false;
        }

        ManagedSequencePoint? current = null;
        ManagedSequencePoint? next = null;
        foreach (ManagedSequencePoint point in symbols.GetSequencePoints(methodToken))
        {
            if (point.IlOffset <= ilOffset)
            {
                current = point;
                continue;
            }

            next = point;
            break;
        }

        if (current is null)
        {
            return false;
        }

        uint endOffset = next is null ? codeSize : checked((uint)next.IlOffset);
        uint startOffset = checked((uint)current.IlOffset);
        if (endOffset <= startOffset)
        {
            return false;
        }

        range = new ManagedStepRange(startOffset, endOffset);
        return true;
    }
}
