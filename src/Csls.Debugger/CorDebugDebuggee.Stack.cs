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
    /// <param name="progress">Receives bounded synchronous traversal and ownership snapshots.</param>
    /// <returns>The selected stack page and exact total only when the walk reaches its end.</returns>
    internal DebugStackTrace GetStackTrace(
        int threadId,
        DebugStopGeneration generation,
        int startFrame,
        int levels,
        CancellationToken cancellationToken,
        IProgress<DebugStackWalkProgress>? progress = null)
    {
        const int maximumPageSize = 4096;
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(threadId);
        ArgumentOutOfRangeException.ThrowIfNegative(startFrame);
        ArgumentOutOfRangeException.ThrowIfNegative(levels);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(levels, maximumPageSize);
        cancellationToken.ThrowIfCancellationRequested();
        using ManagedFrameRegistration registration = _frames.BeginRegistration();
        using var walker = ManagedStackWalker.Open(_debugProcess, threadId);
        var observer = new ManagedStackWalkObserver(progress);
        List<DebugStackFrameInfo> frames = [];
        int inspectedFrames = 0;
        try
        {
            int? totalFrames = null;
            while (true)
            {
                if (!walker.TryTakeFrame(out nint frame, cancellationToken))
                {
                    totalFrames = walker.FrameIndex + 1;
                    break;
                }

                inspectedFrames++;
                if (walker.FrameIndex < startFrame)
                {
                    _ = ComAbi.Release(frame);
                }
                else if (frames.Count == maximumPageSize)
                {
                    _ = ComAbi.Release(frame);
                    throw new InvalidOperationException(
                        $"The stack response exceeds the frame-page limit of {maximumPageSize}. " +
                        "Request a bounded page with startFrame and levels.");
                }
                else
                {
                    // CreateStackFrame consumes this reference even when symbol resolution fails.
                    frames.Add(CreateStackFrame(threadId, walker.FrameIndex, generation, frame));
                }

                if (inspectedFrames % 256 == 0)
                {
                    observer.Report(Snapshot(DebugStackWalkState.Walking));
                }

                cancellationToken.ThrowIfCancellationRequested();
                if (levels > 0 && frames.Count == levels)
                {
                    break;
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
            var result = new DebugStackTrace(frames, totalFrames);
            walker.Dispose();
            observer.Report(Snapshot(DebugStackWalkState.Completed));
            registration.Commit();
            return result;
        }
        catch (Exception failure)
        {
            walker.Dispose();
            registration.Dispose();
            DebugStackWalkState state = failure is OperationCanceledException && cancellationToken.IsCancellationRequested
                ? DebugStackWalkState.Canceled
                : DebugStackWalkState.Failed;
            observer.ReportFailure(Snapshot(state), failure);
            throw;
        }

        DebugStackWalkProgress Snapshot(DebugStackWalkState state) =>
            new(threadId, inspectedFrames, frames.Count, _frames.Count, walker.OwnedInterfaceCount, state);
    }

    /// <summary>
    /// Releases handles created while inspecting a breakpoint that will not stop.
    /// </summary>
    internal void DiscardBreakpointInspection() => ClearFrameHandles();

    private void ClearFrameHandles(bool preserveFrameIdentity = false)
    {
        RetireResultsViewSnapshot();
        _frames.Clear(preserveFrameIdentity);
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
