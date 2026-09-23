using Csls.Debugger.Contracts;

namespace Csls.Debugger;

/// <summary>
/// Retains one generation-bound instruction-pointer destination approved by CoreCLR.
/// </summary>
internal sealed class ManagedGotoTargetHandle
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
    /// Gets or initializes the destination IL offset.
    /// </summary>
    internal required uint IlOffset { get; init; }
}
