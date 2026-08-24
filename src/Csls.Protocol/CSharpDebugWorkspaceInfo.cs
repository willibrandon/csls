namespace Csls.Protocol;

/// <summary>
/// Describes the current workspace lifecycle and immutable Roslyn generation.
/// </summary>
public sealed class CSharpDebugWorkspaceInfo
{
    /// <summary>
    /// Gets the workspace phase as Uninitialized, Configured, Loading, Ready, or ShuttingDown.
    /// </summary>
    public required string Phase { get; init; }

    /// <summary>
    /// Gets the current immutable workspace generation.
    /// </summary>
    public long Generation { get; init; }

    /// <summary>
    /// Gets the loaded workspace folders.
    /// </summary>
    public required IReadOnlyList<CSharpDebugWorkspaceFolderInfo> Folders { get; init; }
}
