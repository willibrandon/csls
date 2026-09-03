using Csls.Debugger.Contracts;
using Csls.Debugger.Interop;

namespace Csls.Debugger;

/// <summary>
/// Resolves and retains generation-bound managed stack-frame handles.
/// </summary>
internal sealed partial class CorDebugDebuggee
{
    private unsafe DebugStackFrameInfo CreateStackFrame(
        int threadId,
        int frameIndex,
        DebugStopGeneration generation,
        nint frame)
    {
        nint ilFrame = 0;
        uint methodToken = 0;
        uint ilOffset = 0;
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
                location = PortablePdbFrameResolver.Resolve(frame, methodToken, ilOffset);
            }
        }
        finally
        {
            if (ilFrame != 0)
            {
                _ = ComAbi.Release(ilFrame);
            }
        }

        (int ThreadId, int FrameIndex) key = (threadId, frameIndex);
        if (_frames.TryGetValue(key, out ManagedFrameHandle? existing))
        {
            _ = ComAbi.Release(frame);
        }
        else
        {
            existing = new ManagedFrameHandle
            {
                Id = checked(++_nextFrameId),
                Generation = generation,
                Pointer = frame,
                ThreadId = threadId,
                FrameIndex = frameIndex,
                MethodToken = methodToken,
                IlOffset = ilOffset,
                ModulePath = location.ModulePath,
                Name = location.Name,
                InstructionReference = $"csls-il-{Guid.NewGuid():N}"
            };
            _frames.Add(key, existing);
            _instructionFrames.Add(
                existing.InstructionReference,
                new ManagedInstructionReferenceHandle
                {
                    Frame = existing,
                    IlOffset = existing.IlOffset
                });
        }

        DebugSourceInfo? source = location.ModulePath is not null &&
            location.SourcePath is not null
            ? _sourceBreakpoints.GetSourceInfo(location.ModulePath, location.SourcePath)
            : null;
        return new DebugStackFrameInfo(
            existing.Id,
            location.Name,
            source,
            location.Line,
            location.Column,
            methodToken == 0 || location.ModulePath is null
                ? null
                : existing.InstructionReference);
    }
}
