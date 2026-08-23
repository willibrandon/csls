namespace Csls.Protocol;

/// <summary>
/// Advertises server capabilities that apply to the complete workspace.
/// </summary>
public sealed record WorkspaceServerCapabilities
{
    /// <summary>
    /// Gets the server's workspace-folder behavior.
    /// </summary>
    public WorkspaceFoldersServerCapabilities? WorkspaceFolders { get; init; }
}
