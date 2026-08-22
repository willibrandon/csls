namespace Csls.Protocol;

/// <summary>
/// Identifies a position within a text document.
/// </summary>
public sealed record TextDocumentPositionParams
{
    /// <summary>
    /// Gets the target text document.
    /// </summary>
    public required TextDocumentIdentifier TextDocument { get; init; }

    /// <summary>
    /// Gets the target UTF-16 position.
    /// </summary>
    public Position Position { get; init; }
}
