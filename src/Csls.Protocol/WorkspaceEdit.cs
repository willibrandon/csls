namespace Csls.Protocol;

/// <summary>
/// Carries version-aware text edits spanning one or more workspace documents.
/// </summary>
public sealed record WorkspaceEdit
{
    /// <summary>
    /// Gets the ordered version-aware document edits.
    /// </summary>
    public required IReadOnlyList<TextDocumentEdit> DocumentChanges { get; init; }
}
