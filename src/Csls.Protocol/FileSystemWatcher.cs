namespace Csls.Protocol;

/// <summary>
/// Describes one workspace file-system glob requested from an LSP client.
/// </summary>
public sealed record FileSystemWatcher
{
    /// <summary>
    /// Gets the workspace-relative glob pattern.
    /// </summary>
    public required string GlobPattern { get; init; }

    /// <summary>
    /// Gets the requested file-system change kinds.
    /// </summary>
    public WatchKind? Kind { get; init; }
}
