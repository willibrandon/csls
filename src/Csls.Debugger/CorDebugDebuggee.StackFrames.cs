using Csls.Debugger.Contracts;
using Csls.Debugger.Interop;

namespace Csls.Debugger;

/// <summary>
/// Resolves logical stopped-frame identities and retains generation-bound native frame bindings.
/// </summary>
internal sealed partial class CorDebugDebuggee
{
    private DebugStackFrameInfo CreateStackFrame(
        int threadId,
        int frameIndex,
        DebugStopGeneration generation,
        nint frame)
    {
        try
        {
            return CreateStackFrameCore(threadId, frameIndex, generation, ref frame);
        }
        finally
        {
            if (frame != 0)
            {
                _ = ComAbi.Release(frame);
            }
        }
    }

    private unsafe DebugStackFrameInfo CreateStackFrameCore(
        int threadId,
        int frameIndex,
        DebugStopGeneration generation,
        ref nint frame)
    {
        nint ilFrame = 0;
        uint methodToken = 0;
        uint ilOffset = 0;
        ulong stackStart = 0;
        ulong stackEnd = 0;
        ManagedFrameLocation location = new()
        {
            Name = "[External Code]",
            Line = 0,
            Column = 0
        };
        try
        {
            if (ComAbi.TryQueryInterface(frame, ICorDebugILFrameAbi.InterfaceId, out ilFrame))
            {
                int mappingResult = 0;
                uint* methodTokenAddress = &methodToken;
                uint* ilOffsetAddress = &ilOffset;
                int* mappingResultAddress = &mappingResult;
                ulong* stackStartAddress = &stackStart;
                ulong* stackEndAddress = &stackEnd;
                CorDebugHResult.ThrowIfFailed(
                    new ICorDebugFrameAbi(frame).GetStackRange(
                        (nint)stackStartAddress,
                        (nint)stackEndAddress),
                    "ICorDebugFrame.GetStackRange");
                stackStart = Volatile.Read(ref *stackStartAddress);
                stackEnd = Volatile.Read(ref *stackEndAddress);
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
                location = ManagedSymbolFrameResolver.Resolve(
                    frame,
                    methodToken,
                    ilOffset,
                    _sourceBreakpoints.FindModule);
            }
        }
        finally
        {
            if (ilFrame != 0)
            {
                _ = ComAbi.Release(ilFrame);
            }
        }

        ManagedFrameIdentity? identity = location.ModuleId is int moduleId &&
            methodToken != 0 && stackStart != 0 && stackEnd != 0
                ? new ManagedFrameIdentity(threadId, stackStart, stackEnd, moduleId, methodToken)
                : null;
        if (_frames.TryGetByPosition(threadId, frameIndex, out ManagedFrameHandle? existing))
        {
            if (existing.Generation != generation || existing.StackStart != stackStart ||
                existing.StackEnd != stackEnd || existing.ModuleId != location.ModuleId ||
                existing.MethodToken != methodToken)
            {
                throw new InvalidOperationException(
                    "The native frame at this stack position changed without an execution boundary.");
            }
        }
        else
        {
            existing = new ManagedFrameHandle
            {
                Id = _frames.GetOrCreateId(identity),
                Generation = generation,
                Pointer = frame,
                ThreadId = threadId,
                FrameIndex = frameIndex,
                StackStart = stackStart,
                StackEnd = stackEnd,
                MethodToken = methodToken,
                IlOffset = ilOffset,
                ModulePath = location.ModulePath,
                ModuleId = location.ModuleId,
                ModuleImage = location.ModuleImage,
                SymbolImage = location.SymbolImage,
                SymbolDeltas = location.SymbolDeltas,
                MetadataDeltas = location.MetadataDeltas,
                SymbolPath = location.ModulePath is null
                    ? null
                    : _sourceBreakpoints.GetSymbolPath(location.ModulePath),
                Name = location.Name,
                InstructionReference = $"csls-il-{Guid.NewGuid():N}",
                InstructionAddressId = checked(++_nextInstructionAddressId),
                ExpressionLanguage = location.ExpressionLanguage
            };
            _frames.Add(existing, identity);
            frame = 0;
            _instructionAddressFrames.Add(existing.InstructionAddressId, existing);
            _instructionFrames.Add(
                existing.InstructionReference,
                new ManagedInstructionReferenceHandle
                {
                    Frame = existing,
                    IlOffset = existing.IlOffset
                });
        }

        DebugSourceInfo? source = location.ModuleId is int sourceModuleId && location.SourcePath is not null
            ? _sourceBreakpoints.GetSourceInfo(sourceModuleId, location.SourcePath)
            : null;
        return new DebugStackFrameInfo(
            existing.Id,
            location.Name,
            source,
            location.Line,
            location.Column,
            methodToken == 0 ||
                location.ModulePath is null && location.ModuleImage is null
                ? null
                : existing.InstructionReference);
    }
}
