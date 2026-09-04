using Csls.Debugger.Interop;

namespace Csls.Debugger;

/// <summary>
/// Applies compiler-validated on-stack replacement decisions during runtime callbacks.
/// </summary>
internal sealed partial class CorDebugManagedCallback
{
    private unsafe ValueTask<bool> HandleFunctionRemapOpportunityAsync(
        nint thread,
        nint oldFunction,
        nint newFunction,
        uint oldIlOffset,
        CancellationToken cancellationToken)
    {
        _ = newFunction;
        _ = cancellationToken;
        if (thread == 0 || oldFunction == 0 ||
            !_sourceBreakpoints.TryGetHotReloadRemap(
                oldFunction,
                oldIlOffset,
                out uint newIlOffset))
        {
            return ValueTask.FromResult(true);
        }

        nint frame = 0;
        nint frame2 = 0;
        try
        {
            nint* frameAddress = &frame;
            CorDebugHResult.ThrowIfFailed(
                new ICorDebugThreadAbi(thread).GetActiveFrame((nint)frameAddress),
                "ICorDebugThread.GetActiveFrame");
            frame = Volatile.Read(ref *frameAddress);
            if (frame == 0 || !ComAbi.TryQueryInterface(
                frame,
                ICorDebugILFrame2Abi.InterfaceId,
                out frame2))
            {
                return ValueTask.FromResult(true);
            }

            CorDebugHResult.ThrowIfFailed(
                new ICorDebugILFrame2Abi(frame2).RemapFunction(newIlOffset),
                "ICorDebugILFrame2.RemapFunction");
            return ValueTask.FromResult(true);
        }
        finally
        {
            if (frame2 != 0)
            {
                _ = ComAbi.Release(frame2);
            }

            if (frame != 0)
            {
                _ = ComAbi.Release(frame);
            }
        }
    }
}
