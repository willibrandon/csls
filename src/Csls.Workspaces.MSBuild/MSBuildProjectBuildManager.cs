using Microsoft.Build.Construction;
using Microsoft.Build.Evaluation;
using Microsoft.Build.Execution;
using Microsoft.CodeAnalysis;
using System.Diagnostics;
using System.Globalization;
using System.Xml;
using MSBuildProject = Microsoft.Build.Evaluation.Project;

namespace Csls.Workspaces;

/// <summary>
/// Evaluates projects once and submits their design-time builds through one MSBuild session.
/// </summary>
internal sealed class MSBuildProjectBuildManager
{
    private static readonly string[] s_requiredTargets = ["Compile", "CoreCompile"];
    private static readonly Lock s_buildManagerCreationGate = new();
    private static readonly XmlReaderSettings s_xmlReaderSettings = new()
    {
        DtdProcessing = DtdProcessing.Prohibit,
        XmlResolver = null
    };
    private static long s_nextBuildIdentity;
    private const string OptionalMarkupTarget = "DesignTimeMarkupCompilation";
    private readonly Action<WorkspaceDiagnosticKind, string> _reportDiagnostic;
    private readonly Dictionary<string, string> _globalProperties;

    /// <summary>
    /// Initializes the design-time build settings for one solution or project group.
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
        ArgumentNullException.ThrowIfNull(projectPaths);
        cancellationToken.ThrowIfCancellationRequested();
        using var projectCollection = new ProjectCollection(
            globalProperties: null,
            loggers: [],
            ToolsetDefinitionLocations.Default);
        using BuildManager buildManager = CreateBuildManager();
        var buildLogger = new MSBuildWorkspaceBuildLogger();
        buildManager.BeginBuild(new BuildParameters(projectCollection)
        {
            EnableNodeReuse = false,
            Loggers = [buildLogger],
            MaxNodeCount = Math.Min(Environment.ProcessorCount, projectPaths.Count)
        });
        try
        {
            return await LoadProjectsAsync(
                projectPaths,
                buildManager,
                buildLogger,
                projectCollection,
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            Process[] workerProcesses = [.. buildManager.GetWorkerProcesses()];
            try
            {
                buildManager.EndBuild();
                await WaitForWorkerProcessesAsync(workerProcesses).ConfigureAwait(false);
            }
            finally
            {
                projectCollection.UnloadAllProjects();
            }
        }
    }

    private static async Task WaitForWorkerProcessesAsync(Process[] workerProcesses)
    {
        if (workerProcesses.Length == 0)
        {
            return;
        }

        using var shutdownSource = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            await Task.WhenAll(workerProcesses.Select(process =>
                process.WaitForExitAsync(shutdownSource.Token))).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (shutdownSource.IsCancellationRequested)
        {
            foreach (Process process in workerProcesses)
            {
                try
                {
                    if (!process.HasExited)
                    {
                        process.Kill(entireProcessTree: true);
                    }
                }
                catch (InvalidOperationException)
                {
                    // The worker exited between the state check and termination request.
                }
            }

            await Task.WhenAll(workerProcesses.Select(process =>
                process.WaitForExitAsync(CancellationToken.None))).ConfigureAwait(false);
        }
    }

    private async Task<IReadOnlyList<MSBuildProjectSnapshot>> LoadProjectsAsync(
        IReadOnlyList<string> projectPaths,
        BuildManager buildManager,
        MSBuildWorkspaceBuildLogger buildLogger,
        ProjectCollection projectCollection,
        CancellationToken cancellationToken)
    {
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
                .. pathsToLoad.Select(path => LoadProjectAsync(
                    path,
                    buildManager,
                    buildLogger,
                    projectCollection,
                    cancellationToken))
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

    private async Task<MSBuildProjectSnapshot[]> LoadProjectAsync(
        string projectPath,
        BuildManager buildManager,
        MSBuildWorkspaceBuildLogger buildLogger,
        ProjectCollection projectCollection,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var loadedProjects = new List<MSBuildProject>();
        try
        {
            ProjectRootElement projectRoot = await ReadProjectRootAsync(
                projectPath,
                projectCollection,
                cancellationToken).ConfigureAwait(false);
            MSBuildProject project = CreateProject(
                projectRoot,
                _globalProperties,
                projectCollection);
            loadedProjects.Add(project);
            if (RequiresLegacyFrameworkFallback(project))
            {
                var fallbackProperties = new Dictionary<string, string>(
                    _globalProperties,
                    StringComparer.OrdinalIgnoreCase);
                if (LegacyFrameworkReferenceResolver.AddFallbackGlobalProperties(
                    fallbackProperties,
                    project.GetPropertyValue("TargetFrameworkIdentifier"),
                    project.GetPropertyValue("TargetFrameworkVersion")))
                {
                    project = CreateProject(
                        projectRoot,
                        fallbackProperties,
                        projectCollection);
                    loadedProjects.Add(project);
                }
            }

            string targetFramework = project.GetPropertyValue("TargetFramework");
            string targetFrameworks = project.GetPropertyValue("TargetFrameworks");
            if (!string.IsNullOrWhiteSpace(targetFramework) ||
                string.IsNullOrWhiteSpace(targetFrameworks))
            {
                ProjectInstance projectInstance = await BuildProjectAsync(
                    project,
                    buildManager,
                    buildLogger,
                    cancellationToken).ConfigureAwait(false);
                return
                [
                    new MSBuildProjectSnapshot(
                        projectPath,
                        MSBuildProjectData.Create(projectInstance),
                        [.. GetInputPaths(project, projectPath)],
                        [.. GetProjectReferencePaths(project, projectPath)])
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
                    frameworkProperties,
                    projectCollection);
                loadedProjects.Add(frameworkProject);
                frameworkProjects[index] = frameworkProject;
            }

            ProjectInstance[] projectInstances = await Task.WhenAll(
                frameworkProjects.Select(frameworkProject =>
                    BuildProjectAsync(
                        frameworkProject,
                        buildManager,
                        buildLogger,
                        cancellationToken))).ConfigureAwait(false);
            var snapshots = new MSBuildProjectSnapshot[frameworks.Length];
            for (int index = 0; index < frameworks.Length; index++)
            {
                snapshots[index] = new MSBuildProjectSnapshot(
                    projectPath,
                    MSBuildProjectData.Create(projectInstances[index]),
                    [.. GetInputPaths(frameworkProjects[index], projectPath)],
                    [.. GetProjectReferencePaths(frameworkProjects[index], projectPath)]);
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
                projectCollection.UnloadProject(project);
            }
        }
    }

    private async Task<ProjectInstance> BuildProjectAsync(
        MSBuildProject project,
        BuildManager buildManager,
        MSBuildWorkspaceBuildLogger buildLogger,
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
            BuildRequestDataFlags.ProvideProjectStateAfterBuild);
        cancellationToken.ThrowIfCancellationRequested();
        BuildSubmission submission = buildManager.PendBuildRequest(request);
        buildLogger.Register(submission.SubmissionId, _reportDiagnostic);
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
            buildLogger.Unregister(submission.SubmissionId);
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
        ProjectCollection projectCollection,
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
            projectCollection);
        root.FullPath = projectPath;
        return root;
    }

    private static string GetNextBuildIdentity() => Interlocked
        .Increment(ref s_nextBuildIdentity)
        .ToString(CultureInfo.InvariantCulture);

    private static BuildManager CreateBuildManager()
    {
        lock (s_buildManagerCreationGate)
        {
            return new BuildManager();
        }
    }

    private static MSBuildProject CreateProject(
        ProjectRootElement projectRoot,
        IDictionary<string, string> globalProperties,
        ProjectCollection projectCollection) =>
        new(
            projectRoot,
            globalProperties,
            toolsVersion: null,
            projectCollection,
            ProjectLoadSettings.RejectCircularImports |
                ProjectLoadSettings.IgnoreEmptyImports |
                ProjectLoadSettings.IgnoreMissingImports |
                ProjectLoadSettings.IgnoreInvalidImports |
                ProjectLoadSettings.DoNotEvaluateElementsWithFalseCondition |
                ProjectLoadSettings.FailOnUnresolvedSdk);

    private static bool RequiresLegacyFrameworkFallback(MSBuildProject project)
    {
        if (!string.Equals(
            project.GetPropertyValue("TargetFrameworkIdentifier"),
            ".NETFramework",
            StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        string frameworkRoot = project.GetPropertyValue("TargetFrameworkRootPath");
        string frameworkVersion = project.GetPropertyValue("TargetFrameworkVersion");
        return string.IsNullOrWhiteSpace(frameworkRoot) ||
            string.IsNullOrWhiteSpace(frameworkVersion) ||
            !File.Exists(Path.Join(
                frameworkRoot,
                ".NETFramework",
                frameworkVersion,
                "mscorlib.dll"));
    }

    private static StringComparer PathComparer =>
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
}
