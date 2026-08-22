namespace Csls.Protocol;

/// <summary>
/// Reports that an opened text document was persisted by the client.
/// </summary>
public sealed record DidSaveTextDocumentParams
{
    /// <summary>
    /// Gets the saved text document.
    /// </summary>
    public required TextDocumentIdentifier TextDocument { get; init; }

    /// <summary>
    /// Gets the complete saved contents when provided by the client.
    /// </summary>
    public string? Text { get; init; }
}
