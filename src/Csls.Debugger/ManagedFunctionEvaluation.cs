namespace Csls.Debugger;

/// <summary>
/// Owns one in-flight CoreCLR function evaluation and its suspended-thread state.
/// </summary>
internal sealed class ManagedFunctionEvaluation
{
    /// <summary>
    /// Gets or sets the owned ICorDebugEval pointer for the active stage.
    /// </summary>
    internal required nint Pointer { get; set; }

    /// <summary>
    /// Gets or initializes the owned ICorDebugFunction pointer for the final call.
    /// </summary>
    internal required nint Function { get; init; }

    /// <summary>
    /// Gets or initializes owned ICorDebugType pointers for object construction.
    /// </summary>
    internal required nint[] TypeArguments { get; init; }

    /// <summary>
    /// Gets or initializes the owned ICorDebugThread pointer selected for evaluation.
    /// </summary>
    internal required nint Thread { get; init; }

    /// <summary>
    /// Gets or initializes the owned strong-handle receiver passed to the final call.
    /// </summary>
    internal required nint Receiver { get; init; }

    /// <summary>
    /// Gets whether the final CoreCLR operation constructs a new managed object.
    /// </summary>
    internal required bool ConstructsObject { get; init; }

    /// <summary>
    /// Gets whether the final CoreCLR operation materializes a string value.
    /// </summary>
    internal required bool MaterializesString { get; init; }

    /// <summary>
    /// Gets or initializes the bound debugger values for user-supplied arguments.
    /// </summary>
    internal required ManagedExpressionValue[] Arguments { get; init; }

    /// <summary>
    /// Gets or initializes owned strong handles for runtime and materialized arguments.
    /// </summary>
    internal required nint[] RuntimeArguments { get; init; }

    /// <summary>
    /// Gets or initializes the managed thread selected for target execution.
    /// </summary>
    internal required int ThreadId { get; init; }

    /// <summary>
    /// Gets or initializes the original debug state of every non-evaluation thread.
    /// </summary>
    internal required IReadOnlyDictionary<int, int> ThreadStates { get; init; }

    /// <summary>
    /// Gets or initializes presentation state when this evaluation constructs a debugger proxy.
    /// </summary>
    internal ManagedDebuggerTypeProxyEvaluation? DebuggerTypeProxy { get; init; }

    /// <summary>
    /// Gets the asynchronous completion delivered by the matching runtime callback.
    /// </summary>
    internal TaskCompletionSource<ManagedFunctionEvaluationResult> Completion { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>
    /// Gets or sets whether cooperative cancellation requested ICorDebugEval.Abort.
    /// </summary>
    internal bool AbortRequested { get; set; }

    /// <summary>
    /// Gets or sets the string argument being materialized by the active stage.
    /// </summary>
    internal int PendingStringArgumentIndex { get; set; } = -1;

    /// <summary>
    /// Gets or sets whether the final user method call has been scheduled.
    /// </summary>
    internal bool MethodCallScheduled { get; set; }
}
