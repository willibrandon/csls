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
    /// Gets or sets the owned ICorDebugFunction pointer for the active call.
    /// </summary>
    internal required nint Function { get; set; }

    /// <summary>
    /// Gets or sets owned declaring and method ICorDebugType arguments for the active call.
    /// </summary>
    internal required nint[] TypeArguments { get; set; }

    /// <summary>
    /// Gets the immutable declared result type captured before the target executes.
    /// </summary>
    internal ManagedBoundType? DeclaredResultType { get; init; }

    /// <summary>
    /// Gets the user-defined conversion whose standard result conversion completes this evaluation.
    /// </summary>
    internal ManagedUserDefinedConversion? ExplicitUserDefinedConversion { get; init; }

    /// <summary>
    /// Gets the selected operator whose lifted operands require nullable extraction.
    /// </summary>
    internal ManagedUserDefinedOperator? UserDefinedOperator { get; init; }

    /// <summary>
    /// Gets the property result's tuple names captured before the target executes.
    /// </summary>
    internal ManagedTupleCustomTypeInfo? ResultTupleCustomTypeInfo { get; init; }

    /// <summary>
    /// Gets the logical source frame used to bind assignments to returned object fields.
    /// </summary>
    internal int? ResultFrameId { get; init; }

    /// <summary>
    /// Gets or initializes the owned ICorDebugThread pointer selected for evaluation.
    /// </summary>
    internal required nint Thread { get; init; }

    /// <summary>
    /// Gets or sets the owned receiver value passed to the active call.
    /// </summary>
    internal required nint Receiver { get; set; }

    /// <summary>
    /// Gets the bound receiver whose temporary storage may require materialization.
    /// </summary>
    internal ManagedExpressionValue? ReceiverValue { get; init; }

    /// <summary>
    /// Gets or sets whether the receiver owns a CoreCLR heap handle rather than a value pointer.
    /// </summary>
    internal required bool ReceiverIsHeapHandle { get; set; }

    /// <summary>
    /// Gets or sets whether the active CoreCLR operation constructs a new managed object.
    /// </summary>
    internal required bool ConstructsObject { get; set; }

    /// <summary>
    /// Gets or sets whether the current call omits the retained receiver argument.
    /// </summary>
    internal bool SuppressReceiver { get; set; }

    /// <summary>
    /// Gets whether the final CoreCLR operation materializes a string value.
    /// </summary>
    internal required bool MaterializesString { get; init; }

    /// <summary>
    /// Gets or sets the bound debugger values for the active call.
    /// </summary>
    internal required ManagedExpressionValue[] Arguments { get; set; }

    /// <summary>
    /// Gets or initializes owned runtime values for runtime and materialized arguments.
    /// </summary>
    internal required nint[] RuntimeArguments { get; init; }

    /// <summary>
    /// Gets whether each runtime argument owns a CoreCLR heap handle.
    /// </summary>
    internal required bool[] RuntimeArgumentIsHeapHandle { get; init; }

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
    /// Gets or initializes state for explicitly requested enumerable presentation.
    /// </summary>
    internal ManagedResultsViewEvaluation? ResultsView { get; init; }

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
    /// Gets or sets the value-type argument awaiting target allocation.
    /// </summary>
    internal int PendingStructuredArgumentIndex { get; set; } = -1;

    /// <summary>
    /// Gets or sets whether a zero-initialized receiver is awaiting target allocation.
    /// </summary>
    internal bool PendingStructuredReceiver { get; set; }

    /// <summary>
    /// Gets or sets whether an exact nullable result allocation awaits its completion callback.
    /// </summary>
    internal bool PendingNullableExplicitResult { get; set; }

    /// <summary>
    /// Gets or sets whether an exact nullable operator result allocation awaits completion.
    /// </summary>
    internal bool PendingNullableOperatorResult { get; set; }

    /// <summary>
    /// Gets or sets the argument whose implicit conversion operator is executing.
    /// </summary>
    internal int PendingUserDefinedConversionArgumentIndex { get; set; } = -1;

    /// <summary>
    /// Gets or sets the owned conversion-operator function for the active stage.
    /// </summary>
    internal nint PendingUserDefinedConversionFunction { get; set; }

    /// <summary>
    /// Gets or sets owned declaring-type arguments for the active conversion stage.
    /// </summary>
    internal nint[] PendingUserDefinedConversionTypeArguments { get; set; } = [];

    /// <summary>
    /// Gets or sets whether the final user method call has been scheduled.
    /// </summary>
    internal bool MethodCallScheduled { get; set; }

    /// <summary>
    /// Gets the current evaluation stage without inspecting the running target or exposing argument values.
    /// </summary>
    internal string OperationDescription
    {
        get
        {
            string operation = PendingNullableOperatorResult
                ? "materializing a nullable operator result"
                : PendingNullableExplicitResult
                ? "materializing a nullable conversion result"
                : PendingStringArgumentIndex >= 0
                ? $"allocating string argument {PendingStringArgumentIndex + 1}"
                : PendingStructuredReceiver
                    ? "allocating a value-type receiver"
                : PendingStructuredArgumentIndex >= 0
                    ? $"allocating value-type argument {PendingStructuredArgumentIndex + 1}"
                : PendingUserDefinedConversionArgumentIndex >= 0
                    ? $"converting argument {PendingUserDefinedConversionArgumentIndex + 1}"
                : MaterializesString
                    ? "allocating a string result"
                    : ConstructsObject
                        ? "invoking a constructor"
                        : "invoking a method";
            return $"{operation} on managed thread {ThreadId} " +
                $"with {ThreadStates.Count} other managed threads suspended at evaluation start";
        }
    }
}
