namespace Csls.Protocol;

/// <summary>
/// Contains the complete contents and identity of an opened text document.
/// </summary>
public sealed record TextDocumentItem
{
    /// <summary>
    /// Gets the document URI.
    /// </summary>
    public required DocumentUri Uri { get; init; }

    /// <summary>
    /// Gets the document language identifier.
    /// </summary>
    public required string LanguageId { get; init; }

    /// <summary>
    /// Gets the client document version.
    /// </summary>
    public int Version { get; init; }

    /// <summary>
    /// Gets the complete document text.
    /// </summary>
    public required string Text { get; init; }
}
