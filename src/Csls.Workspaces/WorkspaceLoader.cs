namespace Csls.Workspaces;

/// <summary>
/// Loads workspace roots through a host-specific project system.
/// </summary>
public abstract class WorkspaceLoader
{
    /// <summary>
    /// Loads an optional preliminary workspace without waiting for the complete project system.
    /// </summary>
    /// <param name="rootPaths">The absolute workspace roots to load.</param>
    /// <param name="buildConfiguration">The build configuration used for project evaluation.</param>
    /// <param name="cancellationToken">The operation cancellation token.</param>
    /// <returns>The preliminary snapshots, or an empty collection when none are available.</returns>
    public virtual Task<IReadOnlyList<WorkspaceFolderSnapshot>> LoadPreliminaryAsync(
        IReadOnlyList<string> rootPaths,
        string buildConfiguration,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(rootPaths);
        ArgumentException.ThrowIfNullOrWhiteSpace(buildConfiguration);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<IReadOnlyList<WorkspaceFolderSnapshot>>([]);
    }

    /// <summary>
    /// Restores dependency state required by the active project system.
    /// </summary>
    /// <param name="rootPaths">The current absolute workspace roots.</param>
    /// <param name="cancellationToken">The restore cancellation token.</param>
    /// <returns>The number of restored workspace entry points.</returns>
    public abstract Task<int> RestoreAsync(
        IReadOnlyList<string> rootPaths,
        CancellationToken cancellationToken);

    /// <summary>
    /// Discovers bounded desktop solution, project, and file-based app entry points.
    /// </summary>
    /// <param name="rootPath">The workspace root or explicit entry point.</param>
    /// <param name="cancellationToken">The discovery cancellation token.</param>
    /// <returns>The preferred ordered workspace entry points.</returns>
    protected static IReadOnlyList<string> DiscoverWorkspaceFiles(
        string rootPath,
        CancellationToken cancellationToken) =>
        WorkspaceDiscovery.Discover(rootPath, cancellationToken);

    /// <summary>
    /// Determines whether a workspace entry point is a file-based C# app.
    /// </summary>
    /// <param name="path">The absolute candidate path.</param>
    /// <returns>True when the path is a file-based C# app.</returns>
    protected static bool IsFileBasedApp(string path) =>
        WorkspaceDiscovery.IsFileBasedApp(path);

    /// <summary>
    /// Counts C# projects in an XML or classic solution without loading MSBuild.
    /// </summary>
    /// <param name="solutionPath">The absolute solution path.</param>
    /// <returns>The number of C# project entries.</returns>
    protected static int CountSolutionProjects(string solutionPath) =>
        SolutionProjectCounter.CountCSharpProjects(solutionPath);

    /// <summary>
    /// Loads one root through the shared loose-file project system.
    /// </summary>
    /// <param name="rootPath">The absolute source file or directory root.</param>
    /// <param name="cancellationToken">The load cancellation token.</param>
    /// <returns>The loaded loose-file workspace snapshot.</returns>
    protected static WorkspaceFolderSnapshot LoadLooseFiles(
        string rootPath,
        CancellationToken cancellationToken) =>
        LooseFileWorkspaceLoader.Load(rootPath, cancellationToken);

    /// <summary>
    /// Loads every requested root into ordered workspace snapshots owned by the caller.
    /// </summary>
    /// <param name="rootPaths">The absolute workspace roots to load.</param>
    /// <param name="buildConfiguration">The build configuration used for project evaluation.</param>
    /// <param name="progress">The optional ordered project progress destination.</param>
    /// <param name="cancellationToken">The operation cancellation token.</param>
    /// <returns>The ordered loaded workspace snapshots.</returns>
    public abstract Task<IReadOnlyList<WorkspaceFolderSnapshot>> LoadAsync(
        IReadOnlyList<string> rootPaths,
        string buildConfiguration,
        IProgress<WorkspaceLoadProgress>? progress,
        CancellationToken cancellationToken);
}
