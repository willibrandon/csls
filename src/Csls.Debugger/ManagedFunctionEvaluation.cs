using Csls.Debugger.Contracts;

namespace Csls.Debugger;

/// <summary>
/// Owns one in-flight CoreCLR function evaluation and its suspended-thread state.
/// </summary>
internal sealed class ManagedFunctionEvaluation
{
    /// <summary>
    /// Gets or initializes the owned ICorDebugEval pointer.
    /// </summary>
    internal required nint Pointer { get; init; }

    /// <summary>
    /// Gets or initializes the stop generation in which the evaluation began.
    /// </summary>
    internal required DebugStopGeneration Generation { get; init; }

    /// <summary>
    /// Gets or initializes the managed thread selected for target execution.
    /// </summary>
    internal required int ThreadId { get; init; }

    /// <summary>
    /// Gets or initializes the original debug state of every non-evaluation thread.
    /// </summary>
    internal required IReadOnlyDictionary<int, int> ThreadStates { get; init; }

    /// <summary>
    /// Gets the asynchronous completion delivered by the matching runtime callback.
    /// </summary>
    internal TaskCompletionSource<DebugEvaluateResult> Completion { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>
    /// Gets or sets whether cooperative cancellation requested ICorDebugEval.Abort.
    /// </summary>
    internal bool AbortRequested { get; set; }
}
