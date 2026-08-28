namespace Csls.Protocol;

/// <summary>
/// Describes one loaded workspace folder for an editor client.
/// </summary>
public sealed class CSharpWorkspaceFolderInfo
{
    /// <summary>
    /// Gets the absolute workspace root path.
    /// </summary>
    public required string RootPath { get; init; }

    /// <summary>
    /// Gets the Roslyn workspace implementation name.
    /// </summary>
    public required string WorkspaceKind { get; init; }

    /// <summary>
    /// Gets the number of loaded projects.
    /// </summary>
    public int ProjectCount { get; init; }

    /// <summary>
    /// Gets the number of loaded source documents.
    /// </summary>
    public int DocumentCount { get; init; }
}
