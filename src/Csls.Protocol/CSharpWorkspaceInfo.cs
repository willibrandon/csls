namespace Csls.Protocol;

/// <summary>
/// Describes the current Roslyn workspace generation for an editor client.
/// </summary>
public sealed class CSharpWorkspaceInfo
{
    /// <summary>
    /// Gets the inspected workspace generation.
    /// </summary>
    public long Generation { get; init; }

    /// <summary>
    /// Gets the loaded workspace folders.
    /// </summary>
    public required IReadOnlyList<CSharpWorkspaceFolderInfo> Workspaces { get; init; }

    /// <summary>
    /// Gets the loaded projects.
    /// </summary>
    public required IReadOnlyList<CSharpWorkspaceProjectInfo> Projects { get; init; }

    /// <summary>
    /// Gets the loaded source documents.
    /// </summary>
    public required IReadOnlyList<CSharpWorkspaceDocumentInfo> Documents { get; init; }
}
