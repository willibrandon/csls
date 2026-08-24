namespace Csls.Protocol;

/// <summary>
/// Identifies a document that the editor is about to save.
/// </summary>
public sealed record WillSaveTextDocumentParams
{
    /// <summary>
    /// Gets the document that will be saved.
    /// </summary>
    public required TextDocumentIdentifier TextDocument { get; init; }

    /// <summary>
    /// Gets the reason for the save.
    /// </summary>
    public required TextDocumentSaveReason Reason { get; init; }
}
