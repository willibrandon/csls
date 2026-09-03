using Csls.Debugger.Contracts;
using Csls.Debugger.Interop;

namespace Csls.Debugger;

/// <summary>
/// Enumerates managed threads and generation-bound stack frames.
/// </summary>
internal sealed partial class CorDebugDebuggee
{
    /// <summary>
    /// Enumerates managed threads while the target is stopped.
    /// </summary>
    /// <returns>A bounded snapshot of current managed threads.</returns>
    internal unsafe IReadOnlyList<DebugThreadInfo> GetThreads()
    {
        const int maximumThreadCount = 4096;
        nint enumerator = 0;
        nint* enumeratorAddress = &enumerator;
        CorDebugHResult.ThrowIfFailed(
            new ICorDebugControllerAbi(_debugProcess).EnumerateThreads((nint)enumeratorAddress),
            "ICorDebugController.EnumerateThreads");
        enumerator = Volatile.Read(ref *enumeratorAddress);
        if (enumerator == 0)
        {
            throw new InvalidOperationException(
                "ICorDebugController.EnumerateThreads returned no enumerator.");
        }

        try
        {
            var result = new List<DebugThreadInfo>();
            var api = new ICorDebugThreadEnumAbi(enumerator);
            while (result.Count < maximumThreadCount)
            {
                nint thread = 0;
                uint fetched = 0;
                nint* threadAddress = &thread;
                uint* fetchedAddress = &fetched;
                int nextResult = api.Next(1, (nint)threadAddress, (nint)fetchedAddress);
                CorDebugHResult.ThrowIfFailed(nextResult, "ICorDebugThreadEnum.Next");
                thread = Volatile.Read(ref *threadAddress);
                fetched = Volatile.Read(ref *fetchedAddress);
                if (fetched == 0)
                {
                    break;
                }

                try
                {
                    uint threadId = 0;
                    uint* threadIdAddress = &threadId;
                    CorDebugHResult.ThrowIfFailed(
                        new ICorDebugThreadAbi(thread).GetID((nint)threadIdAddress),
                        "ICorDebugThread.GetID");
                    int id = checked((int)threadId);
                    result.Add(new DebugThreadInfo(id, $"Thread {id}"));
                }
                finally
                {
                    if (thread != 0)
                    {
                        _ = ComAbi.Release(thread);
                    }
                }
            }

            if (result.Count == maximumThreadCount)
            {
                throw new InvalidOperationException(
                    $"The target exceeds the managed-thread limit of {maximumThreadCount}.");
            }

            return result;
        }
        finally
        {
            _ = ComAbi.Release(enumerator);
        }
    }

    /// <summary>
    /// Enumerates a page of managed frames and retains generation-bound frame handles.
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
                MethodToken = methodToken,
                IlOffset = ilOffset,
                ModulePath = location.ModulePath
            };
            _frames.Add(key, existing);
        }

        return new DebugStackFrameInfo(
            existing.Id,
            location.Name,
            location.SourcePath,
            location.Line,
            location.Column);
    }

    private void ClearFrameHandles()
    {
        foreach (ManagedFrameHandle frame in _frames.Values)
        {
            _ = ComAbi.Release(frame.Pointer);
        }

        _frames.Clear();
        _scopes.Clear();
        foreach (ManagedValueHandle value in _values.Values)
        {
            _ = ComAbi.Release(value.Pointer);
            _ = ComAbi.Release(value.Identity);
        }

        _values.Clear();
        _valueIdentities.Clear();
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
        ManagedFrameHandle frame = _frames.Values.FirstOrDefault(
            candidate => candidate.Id == frameId)
            ?? throw new InvalidOperationException($"Frame {frameId} is stale or unknown.");
        if (frame.Generation != generation)
        {
            throw new InvalidOperationException(
                $"Frame {frameId} belongs to a retired stop generation.");
        }

        return frame;
    }
}
