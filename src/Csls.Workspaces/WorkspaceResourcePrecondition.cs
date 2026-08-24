using Csls.Protocol;

namespace Csls.Workspaces;

/// <summary>
/// Captures the exact existence, version, and content required before an edit is applied.
/// </summary>
public sealed record WorkspaceResourcePrecondition
{
    /// <summary>
    /// Gets the target workspace resource URI.
    /// </summary>
    public required DocumentUri Uri { get; init; }

    /// <summary>
    /// Gets whether the resource must exist when the edit is applied.
    /// </summary>
    public required bool Exists { get; init; }

    /// <summary>
    /// Gets the open-document version, or null when no editor owns the resource.
    /// </summary>
    public int? Version { get; init; }

    /// <summary>
    /// Gets the lowercase SHA-256 content hash required for an existing text resource.
    /// </summary>
    public string? Sha256 { get; init; }
}
