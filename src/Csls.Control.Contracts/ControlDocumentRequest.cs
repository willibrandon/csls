namespace Csls.Control.Contracts;

/// <summary>
/// Identifies one absolute source document for a control operation.
/// </summary>
public sealed record ControlDocumentRequest
{
    /// <summary>
    /// Gets the absolute source document path.
    /// </summary>
    public required string DocumentPath { get; init; }
}
