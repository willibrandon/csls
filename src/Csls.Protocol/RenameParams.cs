namespace Csls.Protocol;

/// <summary>
/// Identifies a symbol and the new identifier requested by the client.
/// </summary>
public sealed record RenameParams
{
    /// <summary>
    /// Gets the document containing the target symbol.
    /// </summary>
    public required TextDocumentIdentifier TextDocument { get; init; }

    /// <summary>
    /// Gets the target UTF-16 document position.
    /// </summary>
    public required Position Position { get; init; }

    /// <summary>
    /// Gets the requested replacement identifier.
    /// </summary>
    public required string NewName { get; init; }
}
