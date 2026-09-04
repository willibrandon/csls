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
        return new ManagedFrameSelection(
            frame.ThreadId,
            frame.FrameIndex,
            frame.MethodToken,
            frame.ModuleId,
            frame.ModulePath,
            frame.Name,
            frame.ExpressionLanguage);
    }

    /// <summary>
    /// Reacquires one frame and proves that target execution did not replace it.
    /// </summary>
    /// <param name="selection">The pre-execution frame identity.</param>
    /// <param name="generation">The stop generation produced by target execution.</param>
    /// <returns>The replacement generation-bound frame handle.</returns>
    internal int ReacquireFrame(
        ManagedFrameSelection selection,
        DebugStopGeneration generation)
    {
        ArgumentNullException.ThrowIfNull(selection);
        DebugStackTrace stack = GetStackTrace(
            selection.ThreadId,
            generation,
            selection.FrameIndex,
            levels: 1);
        DebugStackFrameInfo frameInfo = stack.StackFrames.SingleOrDefault()
            ?? throw new InvalidOperationException(
                "The assignment frame returned or left the managed stack while its value " +
                "was being materialized.");
        ManagedFrameHandle frame = GetFrame(frameInfo.Id, generation);
        bool sameModule = selection.ModuleId is not null && frame.ModuleId is not null
            ? selection.ModuleId == frame.ModuleId
            : string.Equals(selection.ModulePath, frame.ModulePath, StringComparison.Ordinal);
        if (frame.MethodToken != selection.MethodToken ||
            !sameModule ||
            !string.Equals(frame.Name, selection.Name, StringComparison.Ordinal) ||
            frame.ExpressionLanguage != selection.Language)
        {
            throw new InvalidOperationException(
                "The assignment frame changed while its value was being materialized.");
        }

        return frame.Id;
    }
}
