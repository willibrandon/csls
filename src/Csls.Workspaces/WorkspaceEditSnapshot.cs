using Csls.Protocol;

namespace Csls.Workspaces;

/// <summary>
/// Binds one workspace edit to its immutable generation and document preconditions.
/// </summary>
public sealed record WorkspaceEditSnapshot
{
    /// <summary>
    /// Gets the immutable workspace generation that produced the edit.
    /// </summary>
    public required long WorkspaceGeneration { get; init; }

    /// <summary>
    /// Gets the concrete version-aware workspace edit.
    /// </summary>
    public required WorkspaceEdit Edit { get; init; }

    /// <summary>
    /// Gets the exact existence and content preconditions for touched resources.
    /// </summary>
    public required IReadOnlyList<WorkspaceResourcePrecondition> Preconditions { get; init; }
}
