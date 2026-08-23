using Csls.Protocol;

namespace Csls.Control.Contracts;

/// <summary>
/// Exposes a bounded one-use edit plan and every precondition required to apply it.
/// </summary>
public sealed record ControlEditPlan
{
    /// <summary>
    /// Gets the unguessable one-use plan identifier.
    /// </summary>
    public required Guid PlanId { get; init; }

    /// <summary>
    /// Gets the semantic operation that produced the edit.
    /// </summary>
    public required string Operation { get; init; }

    /// <summary>
    /// Gets the workspace generation that produced the edit.
    /// </summary>
    public required long WorkspaceGeneration { get; init; }

    /// <summary>
    /// Gets the instant after which the plan cannot be applied.
    /// </summary>
    public required DateTimeOffset ExpiresAtUtc { get; init; }

    /// <summary>
    /// Gets the concrete version-aware workspace edit.
    /// </summary>
    public required WorkspaceEdit Edit { get; init; }

    /// <summary>
    /// Gets exact version and content-hash preconditions for edited documents.
    /// </summary>
    public required IReadOnlyList<ControlDocumentPrecondition> Preconditions { get; init; }
}
