using Microsoft.Build.Locator;
using Microsoft.CodeAnalysis;
using Microsoft.Extensions.Logging;
using System.Diagnostics;

namespace Csls.Workspaces;

/// <summary>
/// Loads desktop workspaces through the registered MSBuild project system.
/// </summary>
public sealed partial class MSBuildWorkspaceLoader : WorkspaceLoader
{
    private readonly ILogger<MSBuildWorkspaceLoader> _logger;
    private readonly Lock _workspaceCacheGate = new();
    private readonly Dictionary<string, MSBuildWorkspaceCacheEntry> _workspaceCache = new(
        PathComparer);

    /// <summary>
    /// Initializes desktop workspace loading with structured diagnostics.
    /// </summary>
    /// <param name="logger">The desktop workspace loader logger.</param>
    public MSBuildWorkspaceLoader(ILogger<MSBuildWorkspaceLoader> logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
    }

    /// <summary>
    /// Restores every discovered workspace entry point through the real .NET CLI.
    /// </summary>
    /// <param name="rootPaths">The current absolute workspace roots.</param>
    /// <param name="cancellationToken">The restore cancellation token.</param>
    /// <returns>The number of restored workspace entry points.</returns>
    public override Task<int> RestoreAsync(
        IReadOnlyList<string> rootPaths,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(rootPaths);
        string[] entryPoints =
        [
            .. rootPaths
                .SelectMany(root => DiscoverWorkspaceFilesWithLogging(
                    root,
                    cancellationToken))
                .Distinct(PathComparer)
                .Order(PathComparer)
        ];
        return DotNetWorkspaceRestorer.RestoreAsync(entryPoints, _logger, cancellationToken);
    }

    /// <summary>
    /// Loads every root through MSBuild or the loose-file fallback.
    /// </summary>
    /// <param name="rootPaths">The absolute workspace roots to load.</param>
    /// <param name="buildConfiguration">The MSBuild configuration to evaluate.</param>
    /// <param name="progress">The optional ordered project progress destination.</param>
    /// <param name="cancellationToken">The operation cancellation token.</param>
    /// <returns>The ordered loaded workspace snapshots.</returns>
    public override async Task<IReadOnlyList<WorkspaceFolderSnapshot>> LoadAsync(
        IReadOnlyList<string> rootPaths,
        string buildConfiguration,
        IProgress<WorkspaceLoadProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(rootPaths);
        ArgumentException.ThrowIfNullOrWhiteSpace(buildConfiguration);
        (string RootPath, IReadOnlyList<string> WorkspaceFiles)[] loadPlans =
        [
            .. rootPaths
                .Distinct(PathComparer)
                .Select(requestedRoot =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    string rootPath = Path.GetFullPath(requestedRoot);
                    return (
                        rootPath,
                        DiscoverWorkspaceFilesWithLogging(rootPath, cancellationToken));
                })
        ];

        RegisterMSBuild(loadPlans);
        WorkspaceLoadProgressReporter? progressReporter = progress is null
            ? null
            : new WorkspaceLoadProgressReporter(CountExpectedProjects(loadPlans), progress);
        var loadedSnapshots = new WorkspaceFolderSnapshot?[loadPlans.Length];
        Task[] loads =
        [
            .. loadPlans.Select(async (plan, index) =>
                loadedSnapshots[index] = await LoadRootAsync(
                    plan.RootPath,
                    plan.WorkspaceFiles,
                    buildConfiguration,
                    progressReporter,
                    cancellationToken).ConfigureAwait(false))
        ];
        try
        {
            await Task.WhenAll(loads).ConfigureAwait(false);
            return
            [
                .. loadedSnapshots.Select(static snapshot => snapshot ??
                    throw new InvalidOperationException(
                        "Workspace loading completed without a snapshot."))
            ];
        }
        catch
        {
            foreach (WorkspaceFolderSnapshot? snapshot in loadedSnapshots)
            {
                snapshot?.Workspace.Dispose();
            }

            throw;
        }
    }

    private async Task<WorkspaceFolderSnapshot> LoadRootAsync(
        string rootPath,
        IReadOnlyList<string> workspaceFiles,
        string buildConfiguration,
        WorkspaceLoadProgressReporter? progressReporter,
        CancellationToken cancellationToken)
    {
        if (workspaceFiles.Count == 0)
        {
            WorkspaceFolderSnapshot snapshot = LoadLooseFiles(rootPath, cancellationToken);
            Project project = snapshot.Solution.Projects.Single();
            progressReporter?.ReportProject(rootPath, rootPath, project.Name);
            return snapshot;
        }

        (Workspace workspace, Solution solution) = await LoadWorkspaceConcurrentlyAsync(
            rootPath,
            workspaceFiles,
            buildConfiguration,
            progressReporter,
            cancellationToken).ConfigureAwait(false);
        return new WorkspaceFolderSnapshot
        {
            RootPath = rootPath,
            Workspace = workspace,
            Solution = solution
        };
    }

    private IReadOnlyList<string> DiscoverWorkspaceFilesWithLogging(
        string rootPath,
        CancellationToken cancellationToken)
    {
        long startedTimestamp = Stopwatch.GetTimestamp();
        LogWorkspaceDiscoveryStarted(rootPath);
        IReadOnlyList<string> workspaceFiles = DiscoverWorkspaceFiles(
            rootPath,
            cancellationToken);
        long elapsedMilliseconds =
            (long)Stopwatch.GetElapsedTime(startedTimestamp).TotalMilliseconds;
        LogWorkspaceDiscoveryCompleted(workspaceFiles.Count, elapsedMilliseconds);
        return workspaceFiles;
    }

    private static int CountExpectedProjects(
        IReadOnlyList<(string RootPath, IReadOnlyList<string> WorkspaceFiles)> loadPlans)
    {
        int expectedProjectCount = 0;
        foreach ((string _, IReadOnlyList<string> workspaceFiles) in loadPlans)
        {
            expectedProjectCount = checked(expectedProjectCount + (workspaceFiles.Count == 0
                ? 1
                : workspaceFiles.Sum(static workspaceFile =>
                    IsFileBasedApp(workspaceFile) ||
                    workspaceFile.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)
                        ? 1
                        : CountSolutionProjects(workspaceFile))));
        }

        return expectedProjectCount;
    }

    private async Task<(Workspace Workspace, Solution Solution)>
        LoadWorkspaceConcurrentlyAsync(
        string rootPath,
        IReadOnlyList<string> workspaceFiles,
        string buildConfiguration,
        WorkspaceLoadProgressReporter? progressReporter,
        CancellationToken cancellationToken)
    {
        long startedTimestamp = Stopwatch.GetTimestamp();
        foreach (string workspaceFile in workspaceFiles)
        {
            LogWorkspaceLoading(workspaceFile);
        }

        string[] primaryProjectPaths =
        [
            .. workspaceFiles
                .SelectMany(static workspaceFile => IsSolution(workspaceFile)
                    ? SolutionProjectCounter.ReadCSharpProjectPaths(workspaceFile)
                    : workspaceFile.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)
                        ? [workspaceFile]
                        : [])
                .Distinct(PathComparer)
        ];
        string[] fileBasedAppPaths =
        [
            .. workspaceFiles.Where(IsFileBasedApp)
        ];
        Dictionary<string, string> projectFilePaths = fileBasedAppPaths.ToDictionary(
            FileBasedAppProjectLoader.GetMaterializedProjectPath,
            static entryPointPath => entryPointPath,
            PathComparer);
        string cacheKey = string.Join(
            '\0',
            new[] { rootPath, buildConfiguration }.Concat(workspaceFiles));
        MSBuildWorkspaceCacheEntry? cacheEntry;
        lock (_workspaceCacheGate)
        {
            _workspaceCache.TryGetValue(cacheKey, out cacheEntry);
        }

        var snapshotsByPath = new Dictionary<string, IReadOnlyList<MSBuildProjectSnapshot>>(
            PathComparer);
        var projectPathsToBuild = new List<string>();
        var loadedProjectPaths = new List<string>();
        var knownProjectPaths = new HashSet<string>(PathComparer);
        var fileBasedAppsToBuild = new List<string>();
        var projectPathsToInspect = new Queue<(string ProjectPath, string? EntryPointPath)>(
            primaryProjectPaths.Select(static projectPath =>
                CreateProjectPathToInspect(projectPath, null))
                .Concat(projectFilePaths.Select(static pair =>
                    CreateProjectPathToInspect(pair.Key, pair.Value))));
        while (projectPathsToInspect.TryDequeue(
            out (string ProjectPath, string? EntryPointPath) projectToInspect))
        {
            cancellationToken.ThrowIfCancellationRequested();
            string projectPath = projectToInspect.ProjectPath;
            if (!knownProjectPaths.Add(projectPath))
            {
                continue;
            }

            loadedProjectPaths.Add(projectPath);
            if (cacheEntry is not null && cacheEntry.TryGetCurrentSnapshots(
                projectPath,
                out IReadOnlyList<MSBuildProjectSnapshot> cachedSnapshots))
            {
                snapshotsByPath.Add(projectPath, cachedSnapshots);
                foreach (string referencePath in cachedSnapshots.SelectMany(
                    static snapshot => snapshot.ProjectReferencePaths))
                {
                    projectPathsToInspect.Enqueue((referencePath, null));
                }
            }
            else if (projectToInspect.EntryPointPath is not null)
            {
                fileBasedAppsToBuild.Add(projectToInspect.EntryPointPath);
            }
            else
            {
                projectPathsToBuild.Add(projectPath);
            }
        }

        int cachedProjectCount = snapshotsByPath.Count;
        Dictionary<string, string> globalProperties = CreateGlobalProperties(
            rootPath,
            buildConfiguration);
        Task<IReadOnlyList<MSBuildProjectSnapshot>> primaryLoad =
            LoadProjectSnapshotsAsync(
                projectPathsToBuild,
                globalProperties,
                cancellationToken);
        Task<(
            IReadOnlyList<MSBuildProjectSnapshot> Snapshots,
            IReadOnlyDictionary<string, string> LoadedProjectFilePaths)> fileBasedAppLoad =
            FileBasedAppProjectLoader.UseProjectsAsync(
                fileBasedAppsToBuild,
                _logger,
                LogWorkspaceDiagnostic,
                async (materializedProjectPaths, projectFilePaths, loadCancellationToken) =>
                {
                    IReadOnlyList<MSBuildProjectSnapshot> snapshots =
                        await LoadProjectSnapshotsAsync(
                            materializedProjectPaths,
                            globalProperties,
                            loadCancellationToken).ConfigureAwait(false);
                    return (snapshots, projectFilePaths);
                },
                cancellationToken);
        await Task.WhenAll(primaryLoad, fileBasedAppLoad).ConfigureAwait(false);

        int builtProjectCount = 0;
        AddSnapshots(await primaryLoad.ConfigureAwait(false));
        (
            IReadOnlyList<MSBuildProjectSnapshot> appSnapshots,
            IReadOnlyDictionary<string, string> loadedProjectFilePaths) =
            await fileBasedAppLoad.ConfigureAwait(false);
        AddSnapshots(PrepareFileBasedAppSnapshots(appSnapshots, loadedProjectFilePaths));

        MSBuildProjectSnapshot[] projectSnapshots =
        [
            .. loadedProjectPaths.SelectMany(projectPath =>
                snapshotsByPath.GetValueOrDefault(projectPath) ?? [])
        ];
        lock (_workspaceCacheGate)
        {
            _workspaceCache[cacheKey] = new MSBuildWorkspaceCacheEntry(projectSnapshots);
        }

        LogWorkspaceCacheStatus(
            rootPath,
            cachedProjectCount,
            builtProjectCount);
        (Workspace workspace, Solution solution) = MSBuildProjectInfoFactory.Create(
            rootPath,
            projectSnapshots,
            projectFilePaths,
            LogWorkspaceDiagnostic);
        workspace.RegisterWorkspaceFailedHandler(eventArgs =>
            LogWorkspaceDiagnostic(eventArgs.Diagnostic.Kind, eventArgs.Diagnostic.Message));
        foreach (Project project in solution.Projects)
        {
            progressReporter?.ObserveProject(rootPath, project.FilePath ?? rootPath);
        }

        foreach (Project project in solution.Projects)
        {
            progressReporter?.ReportProject(
                rootPath,
                project.FilePath ?? rootPath,
                project.Name);
        }

        long elapsedMilliseconds =
            (long)Stopwatch.GetElapsedTime(startedTimestamp).TotalMilliseconds;
        LogWorkspaceLoaded(rootPath, elapsedMilliseconds, solution.ProjectIds.Count);
        return (workspace, solution);

        void AddSnapshots(IReadOnlyList<MSBuildProjectSnapshot> snapshots)
        {
            foreach (IGrouping<string, MSBuildProjectSnapshot> group in snapshots.GroupBy(
                static snapshot => snapshot.ProjectPath,
                PathComparer))
            {
                snapshotsByPath[group.Key] = group.ToArray();
                builtProjectCount++;
                if (knownProjectPaths.Add(group.Key))
                {
                    loadedProjectPaths.Add(group.Key);
                }
            }
        }
    }

    private static MSBuildProjectSnapshot[] PrepareFileBasedAppSnapshots(
        IReadOnlyList<MSBuildProjectSnapshot> snapshots,
        IReadOnlyDictionary<string, string> projectFilePaths)
    {
        var preparedSnapshots = new MSBuildProjectSnapshot[snapshots.Count];
        for (int index = 0; index < snapshots.Count; index++)
        {
            MSBuildProjectSnapshot snapshot = snapshots[index];
            if (!projectFilePaths.TryGetValue(
                snapshot.ProjectPath,
                out string? entryPointPath))
            {
                preparedSnapshots[index] = snapshot;
                continue;
            }

            string materializedDirectory = Path.GetDirectoryName(snapshot.ProjectPath)!;
            string[] inputPaths =
            [
                .. snapshot.InputPaths
                    .Where(path => !PathComparer.Equals(path, materializedDirectory))
                    .Select(path => PathComparer.Equals(path, snapshot.ProjectPath)
                        ? entryPointPath
                        : path)
                    .Distinct(PathComparer)
            ];
            preparedSnapshots[index] = new MSBuildProjectSnapshot(
                snapshot.ProjectPath,
                snapshot.Project,
                inputPaths,
                snapshot.ProjectReferencePaths);
        }

        return preparedSnapshots;
    }

    private static (string ProjectPath, string? EntryPointPath)
        CreateProjectPathToInspect(
        string projectPath,
        string? entryPointPath) =>
        (projectPath, entryPointPath);

    private async Task<IReadOnlyList<MSBuildProjectSnapshot>> LoadProjectSnapshotsAsync(
        IReadOnlyList<string> projectPaths,
        IReadOnlyDictionary<string, string> globalProperties,
        CancellationToken cancellationToken)
    {
        if (projectPaths.Count == 0)
        {
            return [];
        }

        var buildManager = new MSBuildBuildHostClient(
            globalProperties,
            LogWorkspaceDiagnostic);
        return await buildManager.LoadAsync(projectPaths, cancellationToken)
            .ConfigureAwait(false);
    }

    private static Dictionary<string, string> CreateGlobalProperties(
        string rootPath,
        string buildConfiguration)
    {
        string workspaceDirectory = Directory.Exists(rootPath)
            ? rootPath
            : Path.GetDirectoryName(rootPath)
                ?? throw new InvalidDataException($"Workspace path has no parent: {rootPath}");
        if (!Path.EndsInDirectorySeparator(workspaceDirectory))
        {
            workspaceDirectory += Path.DirectorySeparatorChar;
        }

        var globalProperties = new Dictionary<string, string>(
            StringComparer.OrdinalIgnoreCase)
        {
            ["Configuration"] = buildConfiguration,
            ["EnableWindowsTargeting"] = "true",
            ["SolutionDir"] = workspaceDirectory
        };
        return globalProperties;
    }

    private void RegisterMSBuild(
        IReadOnlyList<(string RootPath, IReadOnlyList<string> WorkspaceFiles)> loadPlans)
    {
        string? firstWorkspaceFile = loadPlans
            .SelectMany(static plan => plan.WorkspaceFiles)
            .FirstOrDefault();
        if (firstWorkspaceFile is null)
        {
            return;
        }

        VisualStudioInstance? instance = MSBuildRegistration.EnsureRegistered(firstWorkspaceFile);
        if (instance is not null)
        {
            LogMSBuildRegistered(
                instance.Name,
                instance.Version,
                instance.MSBuildPath,
                firstWorkspaceFile);
        }
    }

    private static StringComparer PathComparer =>
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private static bool IsSolution(string path) =>
        path.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase) ||
        path.EndsWith(".sln", StringComparison.OrdinalIgnoreCase);

    [LoggerMessage(
        EventId = 1,
        Level = LogLevel.Warning,
        Message = "MSBuild workspace diagnostic {Kind}: {Message}")]
    private partial void LogWorkspaceDiagnostic(WorkspaceDiagnosticKind kind, string message);

    [LoggerMessage(
        EventId = 2,
        Level = LogLevel.Information,
        Message = "Loaded {WorkspaceFile} in {ElapsedMilliseconds} ms with {ProjectCount} projects")]
    private partial void LogWorkspaceLoaded(
        string workspaceFile,
        long elapsedMilliseconds,
        int projectCount);

    [LoggerMessage(
        EventId = 3,
        Level = LogLevel.Information,
        Message = "Discovering C# workspace in {RootPath}")]
    private partial void LogWorkspaceDiscoveryStarted(string rootPath);

    [LoggerMessage(
        EventId = 4,
        Level = LogLevel.Information,
        Message = "Registered MSBuild {Name} {Version} from {Path} for {WorkspaceFile}")]
    private partial void LogMSBuildRegistered(
        string name,
        Version version,
        string path,
        string workspaceFile);

    [LoggerMessage(
        EventId = 5,
        Level = LogLevel.Information,
        Message = "Discovered {EntryPointCount} workspace entry points in {ElapsedMilliseconds} ms")]
    private partial void LogWorkspaceDiscoveryCompleted(
        int entryPointCount,
        long elapsedMilliseconds);

    [LoggerMessage(
        EventId = 6,
        Level = LogLevel.Information,
        Message = "Loading {WorkspaceFile}")]
    private partial void LogWorkspaceLoading(string workspaceFile);

    [LoggerMessage(
        EventId = 7,
        Level = LogLevel.Information,
        Message = "Loaded project state for {WorkspacePath} from cache for {CachedProjectCount} projects and MSBuild for {BuiltProjectCount} projects")]
    private partial void LogWorkspaceCacheStatus(
        string workspacePath,
        int cachedProjectCount,
        int builtProjectCount);
}
