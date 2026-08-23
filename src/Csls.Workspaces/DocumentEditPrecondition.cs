using Csls.Protocol;

namespace Csls.Workspaces;

/// <summary>
/// Captures the version and content hash required before a document edit can be applied.
/// </summary>
public sealed record DocumentEditPrecondition
{
    /// <summary>
    /// Gets the target source document URI.
    /// </summary>
    public required DocumentUri Uri { get; init; }

    /// <summary>
    /// Gets the open-document version, or null when no editor owns the document.
    /// </summary>
    public int? Version { get; init; }

    /// <summary>
    /// Gets the lowercase SHA-256 hash of the exact snapshot text.
    /// </summary>
    public required string Sha256 { get; init; }
}
