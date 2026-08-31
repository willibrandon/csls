using Microsoft.Build.Construction;
using Microsoft.Build.Evaluation;
using Microsoft.Build.Execution;
using Microsoft.CodeAnalysis;
using System.Globalization;
using System.Xml;
using MSBuildProject = Microsoft.Build.Evaluation.Project;

namespace Csls.Workspaces;

/// <summary>
/// Evaluates projects once and submits all design-time builds through one shared MSBuild manager.
/// </summary>
internal sealed class MSBuildProjectBuildManager : IDisposable
{
    private static readonly string[] s_requiredTargets = ["Compile", "CoreCompile"];
    private static readonly Lock s_sharedBuildGate = new();
    private static readonly XmlReaderSettings s_xmlReaderSettings = new()
    {
        DtdProcessing = DtdProcessing.Prohibit,
        XmlResolver = null
    };
    private static BuildManager? s_sharedBuildManager;
    private static ProjectCollection? s_sharedBuildProjectCollection;
    private static MSBuildWorkspaceBuildLogger? s_sharedBuildLogger;
    private static long s_nextBuildIdentity;
    private static int s_sharedBuildUserCount;
    private const string OptionalMarkupTarget = "DesignTimeMarkupCompilation";
    private readonly Action<WorkspaceDiagnosticKind, string> _reportDiagnostic;
    private readonly Dictionary<string, string> _globalProperties;
    private readonly BuildManager _buildManager;
    private readonly MSBuildWorkspaceBuildLogger _buildLogger;
    private int _disposeState;

    /// <summary>
    /// Initializes a shared design-time build session for one solution or project group.
    /// </summary>
    /// <param name="globalProperties">The complete MSBuild global property set.</param>
    /// <param name="reportDiagnostic">The workspace diagnostic destination.</param>
    internal MSBuildProjectBuildManager(
        IReadOnlyDictionary<string, string> globalProperties,
        Action<WorkspaceDiagnosticKind, string> reportDiagnostic)
    {
        ArgumentNullException.ThrowIfNull(globalProperties);
        ArgumentNullException.ThrowIfNull(reportDiagnostic);
        _reportDiagnostic = reportDiagnostic;
        _globalProperties = new Dictionary<string, string>(
            globalProperties,
            StringComparer.OrdinalIgnoreCase)
        {
            ["DesignTimeBuild"] = bool.TrueString,
            ["NonExistentFile"] = "__CslsNonExistentDirectory__/__CslsNonExistentFile__",
            ["BuildProjectReferences"] = bool.FalseString,
            ["BuildingProject"] = bool.FalseString,
            ["ProvideCommandLineArgs"] = bool.TrueString,
            ["SkipCompilerExecution"] = bool.TrueString,
            ["ContinueOnError"] = "ErrorAndContinue",
            ["ShouldUnsetParentConfigurationAndPlatform"] = bool.FalseString,
            ["CslsDesignTimeBuildId"] = GetNextBuildIdentity()
        };
        (_buildManager, _buildLogger) = AcquireSharedBuild();
    }

    private static (BuildManager BuildManager, MSBuildWorkspaceBuildLogger BuildLogger)
        AcquireSharedBuild()
    {
        lock (s_sharedBuildGate)
        {
            if (s_sharedBuildManager is null)
            {
                s_sharedBuildProjectCollection = new ProjectCollection(
                    globalProperties: null,
                    loggers: [],
                    ToolsetDefinitionLocations.Default);
                s_sharedBuildLogger = new MSBuildWorkspaceBuildLogger();
                var buildManager = new BuildManager();
                buildManager.BeginBuild(new BuildParameters(s_sharedBuildProjectCollection)
                {
                    DisableInProcNode = true,
                    EnableNodeReuse = false,
                    Loggers = [s_sharedBuildLogger],
                    MaxNodeCount = Environment.ProcessorCount
                });
                s_sharedBuildManager = buildManager;
            }

            s_sharedBuildUserCount++;
            return (s_sharedBuildManager, s_sharedBuildLogger!);
        }
    }

    /// <summary>
    /// Loads every requested project through concurrent design-time build submissions.
    /// </summary>
    /// <param name="projectPaths">The absolute project paths in solution order.</param>
    /// <param name="cancellationToken">The load cancellation token.</param>
    /// <returns>The completed project states in project and target-framework order.</returns>
    internal async Task<IReadOnlyList<MSBuildProjectSnapshot>> LoadAsync(
        IReadOnlyList<string> projectPaths,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposeState) != 0, this);
        ArgumentNullException.ThrowIfNull(projectPaths);
        var snapshotsByPath = new Dictionary<string, MSBuildProjectSnapshot[]>(PathComparer);
        var orderedPaths = new List<string>();
        var knownPaths = new HashSet<string>(PathComparer);
        string[] pathsToLoad =
        [
            .. projectPaths.Where(path => knownPaths.Add(path))
        ];
        while (pathsToLoad.Length > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Task<MSBuildProjectSnapshot[]>[] loads =
            [
                .. pathsToLoad.Select(path => LoadProjectAsync(path, cancellationToken))
            ];
            MSBuildProjectSnapshot[][] loadedSnapshots = await Task.WhenAll(loads)
                .ConfigureAwait(false);
            var discoveredPaths = new List<string>();
            for (int index = 0; index < pathsToLoad.Length; index++)
            {
                string projectPath = pathsToLoad[index];
                MSBuildProjectSnapshot[] snapshots = loadedSnapshots[index];
                snapshotsByPath[projectPath] = snapshots;
                orderedPaths.Add(projectPath);
                foreach (string referencePath in GetProjectReferencePaths(snapshots)
                    .Where(knownPaths.Add))
                {
                    discoveredPaths.Add(referencePath);
                }
            }

            pathsToLoad = [.. discoveredPaths];
        }

        return
        [
            .. orderedPaths.SelectMany(path => snapshotsByPath[path])
        ];
    }

    /// <summary>
    /// Ends the shared build and releases every evaluated project.
    /// </summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposeState, 1) != 0)
        {
            return;
        }

        ReleaseSharedBuild();
    }

    private async Task<MSBuildProjectSnapshot[]> LoadProjectAsync(
        string projectPath,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var loadedProjects = new List<MSBuildProject>();
        try
        {
            ProjectRootElement projectRoot = await ReadProjectRootAsync(
                projectPath,
                cancellationToken).ConfigureAwait(false);
            MSBuildProject project = CreateProject(projectRoot, _globalProperties);
            loadedProjects.Add(project);
            string targetFramework = project.GetPropertyValue("TargetFramework");
            string targetFrameworks = project.GetPropertyValue("TargetFrameworks");
            if (!string.IsNullOrWhiteSpace(targetFramework) ||
                string.IsNullOrWhiteSpace(targetFrameworks))
            {
                ProjectInstance projectInstance = await BuildProjectAsync(
                    project,
                    cancellationToken).ConfigureAwait(false);
                return
                [
                    new MSBuildProjectSnapshot(
                        projectPath,
                        projectInstance,
                        GetInputPaths(project, projectPath),
                        GetProjectReferencePaths(project, projectPath))
                ];
            }

            string[] frameworks = targetFrameworks.Split(
                ';',
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var frameworkProjects = new MSBuildProject[frameworks.Length];
            for (int index = 0; index < frameworks.Length; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var frameworkProperties = new Dictionary<string, string>(
                    _globalProperties,
                    StringComparer.OrdinalIgnoreCase)
                {
                    ["TargetFramework"] = frameworks[index]
                };
                MSBuildProject frameworkProject = CreateProject(
                    projectRoot,
                    frameworkProperties);
                loadedProjects.Add(frameworkProject);
                frameworkProjects[index] = frameworkProject;
            }

            ProjectInstance[] projectInstances = await Task.WhenAll(
                frameworkProjects.Select(frameworkProject =>
                    BuildProjectAsync(frameworkProject, cancellationToken))).ConfigureAwait(false);
            var snapshots = new MSBuildProjectSnapshot[frameworks.Length];
            for (int index = 0; index < frameworks.Length; index++)
            {
                snapshots[index] = new MSBuildProjectSnapshot(
                    projectPath,
                    projectInstances[index],
                    GetInputPaths(frameworkProjects[index], projectPath),
                    GetProjectReferencePaths(frameworkProjects[index], projectPath));
            }

            return snapshots;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _reportDiagnostic(
                WorkspaceDiagnosticKind.Failure,
                $"Failed to load {projectPath}: {exception.Message}");
            return [];
        }
        finally
        {
            foreach (MSBuildProject project in loadedProjects)
            {
                s_sharedBuildProjectCollection!.UnloadProject(project);
            }
        }
    }

    private async Task<ProjectInstance> BuildProjectAsync(
        MSBuildProject project,
        CancellationToken cancellationToken)
    {
        ProjectInstance projectInstance = project.CreateProjectInstance();
        string? missingTarget = s_requiredTargets.FirstOrDefault(
            target => !projectInstance.Targets.ContainsKey(target));
        if (missingTarget is not null)
        {
            _reportDiagnostic(
                WorkspaceDiagnosticKind.Failure,
                $"Project {project.FullPath} does not contain the {missingTarget} target.");
            return projectInstance;
        }

        string[] targets = projectInstance.Targets.ContainsKey(OptionalMarkupTarget)
            ? [.. s_requiredTargets, OptionalMarkupTarget]
            : s_requiredTargets;
        var request = new BuildRequestData(
            projectInstance,
            targets,
            hostServices: null,
            BuildRequestDataFlags.ProvideProjectStateAfterBuild |
                BuildRequestDataFlags.ReplaceExistingProjectInstance);
        cancellationToken.ThrowIfCancellationRequested();
        BuildSubmission submission = _buildManager.PendBuildRequest(request);
        _buildLogger.Register(submission.SubmissionId, _reportDiagnostic);
        try
        {
            var completion = new TaskCompletionSource<BuildResult>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            submission.ExecuteAsync(
                completedSubmission =>
                {
                    try
                    {
                        BuildResult result = completedSubmission.BuildResult ??
                            throw new InvalidOperationException(
                                "MSBuild completed a submission without a build result.");
                        completion.TrySetResult(result);
                    }
                    catch (InvalidOperationException exception)
                    {
                        completion.TrySetException(exception);
                    }
                },
                context: null);
            BuildResult result = await completion.Task.ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (result.OverallResult == BuildResultCode.Failure && result.Exception is not null)
            {
                _reportDiagnostic(
                    WorkspaceDiagnosticKind.Failure,
                    $"Design-time build failed for {project.FullPath}: {result.Exception.Message}");
            }

            return result.ProjectStateAfterBuild ?? projectInstance;
        }
        finally
        {
            _buildLogger.Unregister(submission.SubmissionId);
        }
    }

    private static HashSet<string> GetInputPaths(
        MSBuildProject project,
        string projectPath)
    {
        var paths = new HashSet<string>(PathComparer)
        {
            projectPath,
            Path.GetDirectoryName(projectPath)!
        };
        foreach (ResolvedImport import in project.Imports.Where(static import =>
            !string.IsNullOrWhiteSpace(import.ImportedProject.FullPath)))
        {
            paths.Add(import.ImportedProject.FullPath);
        }

        string projectDirectory = Path.GetDirectoryName(projectPath)!;
        for (string? configurationDirectory = projectDirectory;
            configurationDirectory is not null;
            configurationDirectory = Directory.GetParent(configurationDirectory)?.FullName)
        {
            paths.Add(Path.Join(configurationDirectory, "Directory.Build.props"));
            paths.Add(Path.Join(configurationDirectory, "Directory.Build.targets"));
            paths.Add(Path.Join(configurationDirectory, "Directory.Packages.props"));
            paths.Add(Path.Join(configurationDirectory, ".editorconfig"));
            paths.Add(Path.Join(configurationDirectory, "global.json"));
        }

        string assetsPath = project.GetPropertyValue("ProjectAssetsFile");
        if (!string.IsNullOrWhiteSpace(assetsPath))
        {
            paths.Add(Path.GetFullPath(assetsPath, projectDirectory));
        }

        foreach (ProjectItem item in project.Items.Where(static item =>
            item.ItemType is "Compile" or "AdditionalFiles" or "EditorConfigFiles"))
        {
            string sourcePath = item.GetMetadataValue("FullPath");
            if (string.IsNullOrWhiteSpace(sourcePath))
            {
                sourcePath = item.EvaluatedInclude;
            }

            sourcePath = Path.IsPathFullyQualified(sourcePath)
                ? Path.GetFullPath(sourcePath)
                : Path.GetFullPath(sourcePath, projectDirectory);

            string? sourceDirectory = Path.GetDirectoryName(sourcePath);
            if (sourceDirectory is not null)
            {
                paths.Add(sourceDirectory);
            }
        }

        return paths;
    }

    private static IEnumerable<string> GetProjectReferencePaths(
        IEnumerable<MSBuildProjectSnapshot> snapshots)
    {
        var paths = new HashSet<string>(PathComparer);
        foreach (MSBuildProjectSnapshot snapshot in snapshots)
        {
            foreach (string path in snapshot.ProjectReferencePaths.Where(paths.Add))
            {
                yield return path;
            }
        }
    }

    private static IEnumerable<string> GetProjectReferencePaths(
        MSBuildProject project,
        string projectPath)
    {
        string projectDirectory = Path.GetDirectoryName(projectPath)!;
        foreach (ProjectItem reference in project.GetItems("ProjectReference"))
        {
            string path = reference.GetMetadataValue("FullPath");
            if (string.IsNullOrWhiteSpace(path))
            {
                path = reference.EvaluatedInclude;
            }

            path = Path.IsPathFullyQualified(path)
                ? Path.GetFullPath(path)
                : Path.GetFullPath(path, projectDirectory);
            if (File.Exists(path))
            {
                yield return path;
            }
        }
    }

    private static async Task<ProjectRootElement> ReadProjectRootAsync(
        string projectPath,
        CancellationToken cancellationToken)
    {
        using var stream = new FileStream(
            projectPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 81920,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var content = new MemoryStream();
        await stream.CopyToAsync(content, cancellationToken).ConfigureAwait(false);
        content.Position = 0;
        using var reader = XmlReader.Create(content, s_xmlReaderSettings);
        var root = ProjectRootElement.Create(
            reader,
            s_sharedBuildProjectCollection!);
        root.FullPath = projectPath;
        return root;
    }

    private static string GetNextBuildIdentity() => Interlocked
        .Increment(ref s_nextBuildIdentity)
        .ToString(CultureInfo.InvariantCulture);

    private static void ReleaseSharedBuild()
    {
        lock (s_sharedBuildGate)
        {
            s_sharedBuildUserCount--;
            if (s_sharedBuildUserCount == 0)
            {
                s_sharedBuildManager!.EndBuild();
                s_sharedBuildManager.Dispose();
                s_sharedBuildProjectCollection!.UnloadAllProjects();
                s_sharedBuildProjectCollection.Dispose();
                s_sharedBuildManager = null;
                s_sharedBuildProjectCollection = null;
                s_sharedBuildLogger = null;
            }
        }
    }

    private static MSBuildProject CreateProject(
        ProjectRootElement projectRoot,
        IDictionary<string, string> globalProperties) =>
        new(
            projectRoot,
            globalProperties,
            toolsVersion: null,
            s_sharedBuildProjectCollection!,
            ProjectLoadSettings.RejectCircularImports |
                ProjectLoadSettings.IgnoreEmptyImports |
                ProjectLoadSettings.IgnoreMissingImports |
                ProjectLoadSettings.IgnoreInvalidImports |
                ProjectLoadSettings.DoNotEvaluateElementsWithFalseCondition |
                ProjectLoadSettings.FailOnUnresolvedSdk);

    private static StringComparer PathComparer =>
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
}
