using Csls.Debugger.Contracts;

namespace Csls.Debugger;

/// <summary>
/// Preserves and validates a selected frame across debugger-owned target execution.
/// </summary>
internal sealed partial class CorDebugDebuggee
{
    /// <summary>
    /// Captures the stable identity of one generation-bound managed frame.
    /// </summary>
    /// <param name="frameId">The generation-bound frame handle.</param>
    /// <param name="generation">The stop generation that owns the frame.</param>
    /// <returns>The frame selection that can be reacquired after target execution.</returns>
    internal ManagedFrameSelection CaptureFrameSelection(
        int frameId,
        DebugStopGeneration generation)
    {
        ManagedFrameHandle frame = GetFrame(frameId, generation);
        _ = _frames.GetIdentity(frame.Id);
        return new ManagedFrameSelection(frame.Id);
    }

    /// <summary>
    /// Reacquires one frame and proves that target execution did not replace it.
    /// </summary>
    /// <param name="selection">The pre-execution frame identity.</param>
    /// <param name="generation">The stop generation produced by target execution.</param>
    /// <param name="cancellationToken">Cancels physical frame reacquisition.</param>
    /// <returns>The replacement generation-bound frame handle.</returns>
    internal int ReacquireFrame(
        ManagedFrameSelection selection,
        DebugStopGeneration generation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(selection);
        return GetFrame(selection.FrameId, generation, cancellationToken).Id;
    }
}
