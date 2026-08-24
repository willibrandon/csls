namespace Csls.Protocol;

/// <summary>
/// Carries version-aware text edits spanning one or more workspace documents.
/// </summary>
public sealed record WorkspaceEdit
{
    /// <summary>
    /// Gets the ordered text edits and filesystem resource operations.
    /// </summary>
    public required IReadOnlyList<WorkspaceDocumentChange> DocumentChanges { get; init; }
}
