using Csls.Debugger.Contracts;
using Csls.Debugger.Interop;

namespace Csls.Debugger;

/// <summary>
/// Reacquires one exact physical activation without retaining the rest of its stack.
/// </summary>
internal sealed partial class CorDebugDebuggee
{
    private void RebindPhysicalFrame(
        int frameId,
        ManagedFrameIdentity identity,
        DebugStopGeneration generation,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using ManagedFrameRegistration registration = _frames.BeginRegistration();
        using var walker = ManagedStackWalker.Open(_debugProcess, identity.ThreadId);
        while (walker.TryTakeFrame(out nint pointer, cancellationToken))
        {
            try
            {
                if (!MatchesPhysicalStackRange(pointer, identity))
                {
                    continue;
                }

                nint consumed = pointer;
                pointer = 0;
                DebugStackFrameInfo frame = CreateStackFrame(identity.ThreadId, walker.FrameIndex, generation, consumed);
                if (frame.Id == frameId)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    registration.Commit();
                    return;
                }
            }
            finally
            {
                if (pointer != 0)
                {
                    _ = ComAbi.Release(pointer);
                }
            }
        }
    }

    private static unsafe bool MatchesPhysicalStackRange(nint frame, ManagedFrameIdentity identity)
    {
        if (!ComAbi.TryQueryInterface(frame, ICorDebugILFrameAbi.InterfaceId, out nint ilFrame))
        {
            return false;
        }

        _ = ComAbi.Release(ilFrame);
        ulong start = 0;
        ulong end = 0;
        ulong* startAddress = &start;
        ulong* endAddress = &end;
        CorDebugHResult.ThrowIfFailed(new ICorDebugFrameAbi(frame).GetStackRange((nint)startAddress, (nint)endAddress),
            "ICorDebugFrame.GetStackRange");
        return Volatile.Read(ref *startAddress) == identity.StackStart &&
            Volatile.Read(ref *endAddress) == identity.StackEnd;
    }
}
