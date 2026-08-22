namespace Csls.Protocol;

/// <summary>
/// Identifies a text document by its URI.
/// </summary>
public sealed record TextDocumentIdentifier
{
    /// <summary>
    /// Gets the document URI.
    /// </summary>
    public required DocumentUri Uri { get; init; }
}
