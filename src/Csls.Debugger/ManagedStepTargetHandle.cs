using Csls.Debugger.Contracts;

namespace Csls.Debugger;

/// <summary>
/// Retains one generation-bound managed call selected by a Step Into request.
/// </summary>
internal sealed class ManagedStepTargetHandle
{
    /// <summary>
    /// Gets or initializes the stop generation that owns the target.
    /// </summary>
    internal required DebugStopGeneration Generation { get; init; }

    /// <summary>
    /// Gets or initializes the owning frame identifier.
    /// </summary>
    internal required int FrameId { get; init; }

    /// <summary>
    /// Gets or initializes the managed thread identifier.
    /// </summary>
    internal required int ThreadId { get; init; }

    /// <summary>
    /// Gets or initializes the active IL offset where target discovery occurred.
    /// </summary>
    internal required uint StartIlOffset { get; init; }

    /// <summary>
    /// Gets or initializes the exclusive IL end of the containing source statement.
    /// </summary>
    internal required uint EndIlOffset { get; init; }

    /// <summary>
    /// Gets or initializes the selected callee's method-definition token.
    /// </summary>
    internal required uint CalleeMethodToken { get; init; }

    /// <summary>
    /// Gets or initializes the selected callee's first visible sequence-point offset.
    /// </summary>
    internal required uint CalleeEntryIlOffset { get; init; }

    /// <summary>
    /// Gets or initializes the number of earlier calls to the same callee in the statement.
    /// </summary>
    internal required int HitsToSkip { get; init; }

}
