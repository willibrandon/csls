namespace Csls.Workspaces;

/// <summary>
/// Retains reusable design-time project states for one workspace and configuration.
/// </summary>
internal sealed class MSBuildWorkspaceCacheEntry
{
    private readonly Dictionary<string, MSBuildProjectSnapshot[]> _snapshotsByPath;

    /// <summary>
    /// Initializes one immutable workspace design-time cache entry.
    /// </summary>
    /// <param name="snapshots">The completed project states to retain.</param>
    internal MSBuildWorkspaceCacheEntry(IReadOnlyList<MSBuildProjectSnapshot> snapshots)
    {
        ArgumentNullException.ThrowIfNull(snapshots);
        _snapshotsByPath = snapshots
            .GroupBy(static snapshot => snapshot.ProjectPath, PathComparer)
            .ToDictionary(
                static group => group.Key,
                static group => group.ToArray(),
                PathComparer);
    }

    /// <summary>
    /// Gets current cached target states for one project path.
    /// </summary>
    /// <param name="projectPath">The absolute project file path.</param>
    /// <param name="snapshots">The reusable target states when all inputs match.</param>
    /// <returns>True when the project does not require another design-time build.</returns>
    internal bool TryGetCurrentSnapshots(
        string projectPath,
        out IReadOnlyList<MSBuildProjectSnapshot> snapshots)
    {
        if (_snapshotsByPath.TryGetValue(
            projectPath,
            out MSBuildProjectSnapshot[]? cachedSnapshots) &&
            cachedSnapshots.Length > 0 &&
            cachedSnapshots.All(static snapshot => snapshot.IsCurrent()))
        {
            snapshots = cachedSnapshots;
            return true;
        }

        snapshots = [];
        return false;
    }

    private static StringComparer PathComparer =>
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
}
