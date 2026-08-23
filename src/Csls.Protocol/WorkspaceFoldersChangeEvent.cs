namespace Csls.Protocol;

/// <summary>
/// Describes workspace folders added to and removed from an LSP session.
/// </summary>
public sealed record WorkspaceFoldersChangeEvent
{
    /// <summary>
    /// Gets the workspace folders added to the session.
    /// </summary>
    public required IReadOnlyList<WorkspaceFolder> Added { get; init; }

    /// <summary>
    /// Gets the workspace folders removed from the session.
    /// </summary>
    public required IReadOnlyList<WorkspaceFolder> Removed { get; init; }
}
