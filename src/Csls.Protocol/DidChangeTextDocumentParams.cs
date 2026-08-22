namespace Csls.Protocol;

/// <summary>
/// Contains ordered content changes for one opened versioned text document.
/// </summary>
public sealed record DidChangeTextDocumentParams
{
    /// <summary>
    /// Gets the changed document and its resulting client version.
    /// </summary>
    public required VersionedTextDocumentIdentifier TextDocument { get; init; }

    /// <summary>
    /// Gets the ordered full-text or incremental content mutations.
    /// </summary>
    public required IReadOnlyList<TextDocumentContentChangeEvent> ContentChanges { get; init; }
}
