namespace Csls.Protocol;

/// <summary>
/// Groups ordered text edits under one document version precondition.
/// </summary>
public sealed record TextDocumentEdit : WorkspaceDocumentChange
{
    /// <summary>
    /// Gets the target document and optional client version.
    /// </summary>
    public required OptionalVersionedTextDocumentIdentifier TextDocument { get; init; }

    /// <summary>
    /// Gets the non-overlapping edits for the target document.
    /// </summary>
    public required IReadOnlyList<TextEdit> Edits { get; init; }
}
