using Microsoft.Build.Locator;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.MSBuild;
using Microsoft.Extensions.Logging;
using System.Diagnostics;
using System.Globalization;

namespace Csls.Workspaces;

/// <summary>
/// Loads desktop workspaces through the registered MSBuild project system.
/// </summary>
public sealed partial class MSBuildWorkspaceLoader : WorkspaceLoader
{
    private readonly ILogger<MSBuildWorkspaceLoader> _logger;
    private readonly Lock _solutionCacheGate = new();
    private readonly Dictionary<string, MSBuildSolutionCacheEntry> _solutionCache = new(
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
                .SelectMany(root => SelectEagerWorkspaceFiles(
                    DiscoverWorkspaceFilesWithLogging(root, cancellationToken)))
                .Distinct(PathComparer)
                .Order(StringComparer.Ordinal)
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
        var loadPlans = new List<(string RootPath, IReadOnlyList<string> WorkspaceFiles)>();
        foreach (string requestedRoot in rootPaths.Distinct(PathComparer))
        {
            cancellationToken.ThrowIfCancellationRequested();
            string rootPath = Path.GetFullPath(requestedRoot);
            loadPlans.Add((
                rootPath,
                SelectEagerWorkspaceFiles(
                    DiscoverWorkspaceFilesWithLogging(rootPath, cancellationToken))));
        }

        RegisterMSBuild(loadPlans);
        WorkspaceLoadProgressReporter? progressReporter = progress is null
            ? null
            : new WorkspaceLoadProgressReporter(CountExpectedProjects(loadPlans), progress);
        var snapshots = new List<WorkspaceFolderSnapshot>();
        try
        {
            foreach ((string rootPath, IReadOnlyList<string> workspaceFiles) in loadPlans)
            {
                if (workspaceFiles.Count == 0)
                {
                    WorkspaceFolderSnapshot snapshot = LoadLooseFiles(
                        rootPath,
                        cancellationToken);
                    Project project = snapshot.Solution.Projects.Single();
                    progressReporter?.ReportProject(rootPath, rootPath, project.Name);
                    snapshots.Add(snapshot);
                    continue;
                }

                List<IReadOnlyList<string>> loadGroups = CreateLoadGroups(
                    workspaceFiles);
                var loadedWorkspaces = new (Workspace Workspace, Solution Solution)?[
                    loadGroups.Count];
                try
                {
                    await Parallel.ForAsync(
                        0,
                        loadGroups.Count,
                        cancellationToken,
                        async (index, parallelCancellationToken) =>
                        {
                            loadedWorkspaces[index] = await LoadWorkspaceFilesAsync(
                                loadGroups[index],
                                buildConfiguration,
                                string.Concat(
                                    rootPath,
                                    "\0",
                                    index.ToString(CultureInfo.InvariantCulture)),
                                progressReporter,
                                parallelCancellationToken).ConfigureAwait(false);
                        }).ConfigureAwait(false);

                    for (int index = 0; index < loadGroups.Count; index++)
                    {
                        (Workspace Workspace, Solution Solution) loaded = loadedWorkspaces[index]
                            ?? throw new InvalidOperationException(
                                "Workspace loading did not produce a result for " +
                                string.Join(", ", loadGroups[index]) + '.');
                        snapshots.Add(new WorkspaceFolderSnapshot
                        {
                            RootPath = rootPath,
                            Workspace = loaded.Workspace,
                            Solution = loaded.Solution
                        });
                        loadedWorkspaces[index] = null;
                    }
                }
                finally
                {
                    foreach ((Workspace Workspace, Solution Solution)? loaded in loadedWorkspaces)
                    {
                        loaded?.Workspace.Dispose();
                    }
                }
            }

            return snapshots;
        }
        catch
        {
            Dispose(snapshots);
            throw;
        }
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

    private static List<IReadOnlyList<string>> CreateLoadGroups(
        IReadOnlyList<string> workspaceFiles)
    {
        var standaloneFiles = new List<string>();
        var groups = new List<IReadOnlyList<string>>();
        foreach (string workspaceFile in workspaceFiles)
        {
            if (workspaceFile.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase) ||
                workspaceFile.EndsWith(".sln", StringComparison.OrdinalIgnoreCase))
            {
                groups.Add([workspaceFile]);
            }
            else
            {
                standaloneFiles.Add(workspaceFile);
            }
        }

        if (standaloneFiles.Count > 0)
        {
            groups.Add(standaloneFiles);
        }

        groups.Sort(static (left, right) => StringComparer.Ordinal.Compare(left[0], right[0]));
        return groups;
    }

    private static IReadOnlyList<string> SelectEagerWorkspaceFiles(
        IReadOnlyList<string> workspaceFiles)
    {
        bool containsSolution = workspaceFiles.Any(static workspaceFile =>
            workspaceFile.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase) ||
            workspaceFile.EndsWith(".sln", StringComparison.OrdinalIgnoreCase));
        return containsSolution
            ?
            [
                .. workspaceFiles.Where(static workspaceFile =>
                    !IsFileBasedApp(workspaceFile))
            ]
            : workspaceFiles;
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

    private static void Dispose(IEnumerable<WorkspaceFolderSnapshot> snapshots)
    {
        foreach (WorkspaceFolderSnapshot snapshot in snapshots)
        {
            snapshot.Workspace.Dispose();
        }
    }

    private async Task<(Workspace Workspace, Solution Solution)> LoadWorkspaceFilesAsync(
        IReadOnlyList<string> workspaceFiles,
        string buildConfiguration,
        string loadIdentity,
        WorkspaceLoadProgressReporter? progressReporter,
        CancellationToken cancellationToken)
    {
        if (workspaceFiles.Count == 1 && IsSolution(workspaceFiles[0]))
        {
            return await LoadSolutionConcurrentlyAsync(
                workspaceFiles[0],
                buildConfiguration,
                loadIdentity,
                progressReporter,
                cancellationToken).ConfigureAwait(false);
        }

        var globalProperties = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Configuration"] = buildConfiguration,
            ["EnableWindowsTargeting"] = "true"
        };
        LegacyFrameworkReferenceResolver.AddGlobalProperties(globalProperties);
        var workspace = MSBuildWorkspace.Create(globalProperties);
        try
        {
            var fileBasedAppPaths = new Dictionary<ProjectId, string>();
            workspace.RegisterWorkspaceFailedHandler(eventArgs =>
                LogWorkspaceDiagnostic(eventArgs.Diagnostic.Kind, eventArgs.Diagnostic.Message));
            Dictionary<string, (string EvaluatedProjectPath, string Content)>
                preparedFileBasedApps = await PrepareFileBasedAppsAsync(
                    workspaceFiles,
                    cancellationToken).ConfigureAwait(false);
            Solution solution = workspace.CurrentSolution;
            foreach (string workspaceFile in workspaceFiles)
            {
                long workspaceFileStartedTimestamp = Stopwatch.GetTimestamp();
                LogWorkspaceLoading(workspaceFile);
                IProgress<ProjectLoadProgress>? projectProgress = progressReporter?
                    .CreateObserver(loadIdentity);
                int previousProjectCount = solution.ProjectIds.Count;
                Project? existingProject = solution.Projects.FirstOrDefault(project =>
                    string.Equals(project.FilePath, workspaceFile, PathComparison));
                if (existingProject is not null)
                {
                    progressReporter?.ReportProject(loadIdentity, workspaceFile);
                }
                else if (IsFileBasedApp(workspaceFile))
                {
                    (string evaluatedProjectPath, string content) =
                        preparedFileBasedApps[workspaceFile];
                    Project project = await FileBasedAppProjectLoader.OpenPreparedProjectAsync(
                        workspace,
                        workspaceFile,
                        evaluatedProjectPath,
                        content,
                        LogWorkspaceDiagnostic,
                        cancellationToken).ConfigureAwait(false);
                    solution = project.Solution;
                    fileBasedAppPaths[project.Id] = workspaceFile;
                }
                else if (workspaceFile.EndsWith(
                    ".csproj",
                    StringComparison.OrdinalIgnoreCase))
                {
                    Project project = await workspace
                        .OpenProjectAsync(workspaceFile, projectProgress, cancellationToken)
                        .ConfigureAwait(false);
                    solution = project.Solution;
                }
                else
                {
                    solution = await workspace
                        .OpenSolutionAsync(workspaceFile, projectProgress, cancellationToken)
                        .ConfigureAwait(false);
                }

                if (progressReporter is not null)
                {
                    foreach (string projectPath in solution.Projects
                        .Select(static project => project.FilePath)
                        .OfType<string>())
                    {
                        progressReporter.ReportProject(loadIdentity, projectPath);
                    }
                }

                long elapsedMilliseconds = (long)Stopwatch
                    .GetElapsedTime(workspaceFileStartedTimestamp)
                    .TotalMilliseconds;
                LogWorkspaceLoaded(
                    workspaceFile,
                    elapsedMilliseconds,
                    solution.ProjectIds.Count - previousProjectCount);
            }

            foreach ((ProjectId projectId, string entryPointPath) in fileBasedAppPaths)
            {
                solution = solution.WithProjectFilePath(projectId, entryPointPath);
            }

            return (workspace, solution);
        }
        catch
        {
            workspace.Dispose();
            throw;
        }
    }

    private async Task<(Workspace Workspace, Solution Solution)> LoadSolutionConcurrentlyAsync(
        string solutionPath,
        string buildConfiguration,
        string loadIdentity,
        WorkspaceLoadProgressReporter? progressReporter,
        CancellationToken cancellationToken)
    {
        long startedTimestamp = Stopwatch.GetTimestamp();
        LogWorkspaceLoading(solutionPath);
        IReadOnlyList<string> projectPaths = SolutionProjectCounter.ReadCSharpProjectPaths(
            solutionPath);
        string cacheKey = string.Concat(solutionPath, "\0", buildConfiguration);
        MSBuildSolutionCacheEntry? cacheEntry;
        lock (_solutionCacheGate)
        {
            _solutionCache.TryGetValue(cacheKey, out cacheEntry);
        }

        var snapshotsByPath = new Dictionary<string, IReadOnlyList<MSBuildProjectSnapshot>>(
            PathComparer);
        var projectPathsToBuild = new List<string>();
        var loadedProjectPaths = new List<string>();
        var knownProjectPaths = new HashSet<string>(PathComparer);
        var projectPathsToInspect = new List<string>(projectPaths);
        for (int index = 0; index < projectPathsToInspect.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string projectPath = projectPathsToInspect[index];
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
                    projectPathsToInspect.Add(referencePath);
                }
            }
            else
            {
                projectPathsToBuild.Add(projectPath);
            }
        }

        int cachedProjectCount = snapshotsByPath.Count;
        int builtProjectCount = 0;

        var globalProperties = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Configuration"] = buildConfiguration,
            ["EnableWindowsTargeting"] = "true",
            ["SolutionDir"] = Path.EndsInDirectorySeparator(
                Path.GetDirectoryName(solutionPath)!)
                ? Path.GetDirectoryName(solutionPath)!
                : Path.GetDirectoryName(solutionPath)! + Path.DirectorySeparatorChar
        };
        LegacyFrameworkReferenceResolver.AddGlobalProperties(globalProperties);
        if (projectPathsToBuild.Count > 0)
        {
            using var buildManager = new MSBuildProjectBuildManager(
                globalProperties,
                LogWorkspaceDiagnostic);
            IReadOnlyList<MSBuildProjectSnapshot> builtSnapshots = await buildManager
                .LoadAsync(projectPathsToBuild, cancellationToken)
                .ConfigureAwait(false);
            foreach (IGrouping<string, MSBuildProjectSnapshot> group in builtSnapshots.GroupBy(
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

        MSBuildProjectSnapshot[] projectSnapshots =
        [
            .. loadedProjectPaths.SelectMany(projectPath =>
                snapshotsByPath.GetValueOrDefault(projectPath) ?? [])
        ];
        lock (_solutionCacheGate)
        {
            _solutionCache[cacheKey] = new MSBuildSolutionCacheEntry(projectSnapshots);
        }

        LogSolutionCacheStatus(
            solutionPath,
            cachedProjectCount,
            builtProjectCount);
        (Workspace workspace, Solution solution) = MSBuildProjectInfoFactory.Create(
            solutionPath,
            projectSnapshots,
            LogWorkspaceDiagnostic);
        workspace.RegisterWorkspaceFailedHandler(eventArgs =>
            LogWorkspaceDiagnostic(eventArgs.Diagnostic.Kind, eventArgs.Diagnostic.Message));
        foreach (Project project in solution.Projects)
        {
            progressReporter?.ObserveProject(
                loadIdentity,
                project.FilePath ?? solutionPath);
        }

        foreach (Project project in solution.Projects)
        {
            progressReporter?.ReportProject(
                loadIdentity,
                project.FilePath ?? solutionPath,
                project.Name);
        }

        long elapsedMilliseconds =
            (long)Stopwatch.GetElapsedTime(startedTimestamp).TotalMilliseconds;
        LogWorkspaceLoaded(solutionPath, elapsedMilliseconds, solution.ProjectIds.Count);

        return (workspace, solution);
    }

    private async Task<Dictionary<string, (string EvaluatedProjectPath, string Content)>>
        PrepareFileBasedAppsAsync(
            IReadOnlyList<string> workspaceFiles,
            CancellationToken cancellationToken)
    {
        string[] entryPointPaths =
        [
            .. workspaceFiles.Where(IsFileBasedApp)
        ];
        (string EvaluatedProjectPath, string Content)[] preparedProjects = await Task.WhenAll(
            entryPointPaths.Select(entryPointPath =>
                FileBasedAppProjectLoader.PrepareProjectAsync(
                    entryPointPath,
                    _logger,
                    LogWorkspaceDiagnostic,
                    cancellationToken))).ConfigureAwait(false);

        var result = new Dictionary<string, (string EvaluatedProjectPath, string Content)>(
            entryPointPaths.Length,
            PathComparer);
        for (int index = 0; index < entryPointPaths.Length; index++)
        {
            result.Add(
                entryPointPaths[index],
                preparedProjects[index]);
        }

        return result;
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

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

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
        Message = "Loaded project state for {SolutionPath} from cache for {CachedProjectCount} projects and MSBuild for {BuiltProjectCount} projects")]
    private partial void LogSolutionCacheStatus(
        string solutionPath,
        int cachedProjectCount,
        int builtProjectCount);
}
