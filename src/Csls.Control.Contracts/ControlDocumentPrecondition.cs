namespace Csls.Control.Contracts;

/// <summary>
/// Describes the exact document state required before an edit plan can be applied.
/// </summary>
public sealed record ControlDocumentPrecondition
{
    /// <summary>
    /// Gets the absolute source document path.
    /// </summary>
    public required string DocumentPath { get; init; }

    /// <summary>
    /// Gets the open-document version, or null for a closed document.
    /// </summary>
    public int? Version { get; init; }

    /// <summary>
    /// Gets the lowercase SHA-256 hash of the exact snapshot text.
    /// </summary>
    public required string Sha256 { get; init; }
}
