using Csls.Debugger.Contracts;

namespace Csls.Debugger;

/// <summary>
/// Maps one variable-container handle to a frame and stop generation.
/// </summary>
internal sealed class ManagedScopeHandle
{
    /// <summary>
    /// Gets or initializes the session-local container identifier.
    /// </summary>
    internal required int Id { get; init; }

    /// <summary>
    /// Gets or initializes the owning frame identifier.
    /// </summary>
    internal required int FrameId { get; init; }

    /// <summary>
    /// Gets or initializes the owning stop generation.
    /// </summary>
    internal required DebugStopGeneration Generation { get; init; }

    /// <summary>
    /// Gets or initializes the runtime-backed scope kind.
    /// </summary>
    internal required ManagedScopeKind Kind { get; init; }
}
