using Csls.Debugger.Contracts;
using Csls.Debugger.Interop;

namespace Csls.Debugger;

/// <summary>
/// Enumerates logical managed frames with generation-bound native bindings.
/// </summary>
internal sealed partial class CorDebugDebuggee
{
    /// <summary>
    /// Enumerates managed frames and retains native bindings for the current generation.
    /// </summary>
    /// <param name="threadId">The runtime thread identifier.</param>
    /// <param name="generation">The current debugger stop generation.</param>
    /// <param name="startFrame">The zero-based first frame to return.</param>
    /// <param name="levels">The maximum count, or zero for all remaining frames.</param>
    /// <returns>The selected stack page and complete frame count.</returns>
    internal unsafe DebugStackTrace GetStackTrace(
        int threadId,
        DebugStopGeneration generation,
        int startFrame,
        int levels)
    {
        const int maximumFrameCount = 4096;
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(threadId);
        ArgumentOutOfRangeException.ThrowIfNegative(startFrame);
        ArgumentOutOfRangeException.ThrowIfNegative(levels);

        const int endOfStackHResult = 0x00131324;
        const int maximumWalkCount = 16 * 1024;
        nint thread = 0;
        nint thread3 = 0;
        nint stackWalk = 0;
        try
        {
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

            if (!ComAbi.TryQueryInterface(thread, ICorDebugThread3Abi.InterfaceId, out thread3))
            {
                throw new InvalidOperationException(
                    "The target runtime does not expose ICorDebugThread3 stack walking.");
            }

            nint* stackWalkAddress = &stackWalk;
            CorDebugHResult.ThrowIfFailed(
                new ICorDebugThread3Abi(thread3).CreateStackWalk((nint)stackWalkAddress),
                "ICorDebugThread3.CreateStackWalk");
            stackWalk = Volatile.Read(ref *stackWalkAddress);
            if (stackWalk == 0)
            {
                throw new InvalidOperationException(
                    "ICorDebugThread3.CreateStackWalk returned no stack walker.");
            }

            List<DebugStackFrameInfo> frames = [];
            int frameIndex = 0;
            var walker = new ICorDebugStackWalkAbi(stackWalk);
            for (int walkIndex = 0; walkIndex < maximumWalkCount; walkIndex++)
            {
                nint frame = 0;
                nint* frameAddress = &frame;
                int frameResult = walker.GetFrame((nint)frameAddress);
                CorDebugHResult.ThrowIfFailed(frameResult, "ICorDebugStackWalk.GetFrame");
                frame = Volatile.Read(ref *frameAddress);
                if (frameResult == 0 && frame != 0)
                {
                    bool selected = frameIndex >= startFrame &&
                        (levels == 0 || frames.Count < levels);
                    if (selected)
                    {
                        frames.Add(CreateStackFrame(
                            threadId,
                            frameIndex,
                            generation,
                            frame));
                        frame = 0;
                    }

                    frameIndex++;
                }

                if (frame != 0)
                {
                    _ = ComAbi.Release(frame);
                }

                if (frameIndex > maximumFrameCount)
                {
                    throw new InvalidOperationException(
                        $"The target exceeds the managed-frame limit of {maximumFrameCount}.");
                }

                int nextResult = walker.Next();
                if (nextResult == endOfStackHResult)
                {
                    return new DebugStackTrace(frames, frameIndex);
                }

                CorDebugHResult.ThrowIfFailed(nextResult, "ICorDebugStackWalk.Next");
            }

            throw new InvalidOperationException(
                $"The target exceeds the stack-walk limit of {maximumWalkCount}.");
        }
        finally
        {
            if (stackWalk != 0)
            {
                _ = ComAbi.Release(stackWalk);
            }

            if (thread3 != 0)
            {
                _ = ComAbi.Release(thread3);
            }

            if (thread != 0)
            {
                _ = ComAbi.Release(thread);
            }
        }
    }

    /// <summary>
    /// Releases handles created while inspecting a breakpoint that will not stop.
    /// </summary>
    internal void DiscardBreakpointInspection() => ClearFrameHandles();

    private void ClearFrameHandles(bool preserveFrameIdentity = false)
    {
        RetireResultsViewSnapshot();
        _frames.Clear(preserveFrameIdentity);
        _instructionFrames.Clear();
        _instructionAddressFrames.Clear();
        _stepTargets.Clear();
        _gotoTargets.Clear();
        _scopes.Clear();
        foreach (ManagedValueHandle value in _values.Values)
        {
            _ = ComAbi.Release(value.Pointer);
            _ = ComAbi.Release(value.Identity);
        }

        _values.Clear();
        _valueIdentities.Clear();
        _heapValueOrigins.Clear();
        _memoryValues.Clear();
    }

    private void ReleaseActiveStepper(bool deactivate)
    {
        nint stepper = Interlocked.Exchange(ref _activeStepper, 0);
        nint identity = Interlocked.Exchange(ref _activeStepperIdentity, 0);
        if (stepper != 0)
        {
            if (deactivate)
            {
                _ = new ICorDebugStepperAbi(stepper).Deactivate();
            }

            _ = ComAbi.Release(stepper);
        }

        if (identity != 0)
        {
            _ = ComAbi.Release(identity);
        }
    }

    private ManagedFrameHandle GetFrame(int frameId, DebugStopGeneration generation)
    {
        if (!_frames.TryGetCurrent(frameId, out ManagedFrameHandle? frame))
        {
            ManagedFrameIdentity identity = _frames.GetIdentity(frameId);
            _ = GetStackTrace(identity.ThreadId, generation, startFrame: 0, levels: 0);
            if (!_frames.TryGetCurrent(frameId, out frame))
            {
                _frames.RetireIdentity(frameId);
                throw new InvalidOperationException(
                    $"Frame {frameId} no longer identifies the same physical managed activation.");
            }
        }

        if (frame.Generation != generation)
        {
            throw new InvalidOperationException(
                $"Frame {frameId} belongs to a retired stop generation.");
        }

        return frame;
    }
}
