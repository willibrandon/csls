namespace Csls.Protocol;

/// <summary>
/// Describes one loaded workspace folder in the current Roslyn generation.
/// </summary>
public sealed class CSharpDebugWorkspaceFolderInfo
{
    /// <summary>
    /// Gets the normalized file URI for the workspace folder.
    /// </summary>
    public required DocumentUri Uri { get; init; }

    /// <summary>
    /// Gets the display name of the workspace folder.
    /// </summary>
    public required string Name { get; init; }

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
