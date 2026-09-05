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
    /// <param name="cancellationToken">Cancels native stack enumeration.</param>
    /// <returns>The selected stack page and exact total only when the walk reaches its end.</returns>
    internal DebugStackTrace GetStackTrace(
        int threadId,
        DebugStopGeneration generation,
        int startFrame,
        int levels,
        CancellationToken cancellationToken)
    {
        const int maximumPageSize = 4096;
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(threadId);
        ArgumentOutOfRangeException.ThrowIfNegative(startFrame);
        ArgumentOutOfRangeException.ThrowIfNegative(levels);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(levels, maximumPageSize);
        cancellationToken.ThrowIfCancellationRequested();
        using var walker = ManagedStackWalker.Open(_debugProcess, threadId);
        List<DebugStackFrameInfo> frames = [];
        while (walker.TryTakeFrame(out nint frame, cancellationToken))
        {
            if (walker.FrameIndex < startFrame)
            {
                _ = ComAbi.Release(frame);
                continue;
            }

            if (frames.Count == maximumPageSize)
            {
                _ = ComAbi.Release(frame);
                throw new InvalidOperationException(
                    $"The stack response exceeds the frame-page limit of {maximumPageSize}. " +
                    "Request a bounded page with startFrame and levels.");
            }

            // CreateStackFrame consumes this reference even when symbol resolution fails.
            frames.Add(CreateStackFrame(threadId, walker.FrameIndex, generation, frame));
            if (levels > 0 && frames.Count == levels)
            {
                return new DebugStackTrace(frames, TotalFrames: null);
            }
        }

        return new DebugStackTrace(frames, walker.FrameIndex + 1);
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

    private ManagedFrameHandle GetFrame(
        int frameId,
        DebugStopGeneration generation,
        CancellationToken cancellationToken = default)
    {
        if (!_frames.TryGetCurrent(frameId, out ManagedFrameHandle? frame))
        {
            ManagedFrameIdentity identity = _frames.GetIdentity(frameId);
            RebindPhysicalFrame(frameId, identity, generation, cancellationToken);
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
