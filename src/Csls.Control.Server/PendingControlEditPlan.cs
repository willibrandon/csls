using Csls.Workspaces;

namespace Csls.Control;

/// <summary>
/// Retains one unexpired workspace edit snapshot until a control client applies it once.
/// </summary>
internal sealed record PendingControlEditPlan
{
    /// <summary>
    /// Gets the immutable workspace edit and application preconditions.
    /// </summary>
    internal required WorkspaceEditSnapshot Snapshot { get; init; }

    /// <summary>
    /// Gets the instant after which the plan must be rejected.
    /// </summary>
    internal required DateTimeOffset ExpiresAtUtc { get; init; }
}
