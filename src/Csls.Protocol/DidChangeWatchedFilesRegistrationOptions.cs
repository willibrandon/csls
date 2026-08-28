namespace Csls.Protocol;

/// <summary>
/// Configures dynamically registered workspace file-system watchers.
/// </summary>
public sealed record DidChangeWatchedFilesRegistrationOptions
{
    /// <summary>
    /// Gets the ordered workspace file-system watchers.
    /// </summary>
    public required IReadOnlyList<FileSystemWatcher> Watchers { get; init; }
}
