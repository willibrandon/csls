using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace Csls.Workspaces;

/// <summary>
/// Loads synchronized browser files into project-shaped Roslyn workspaces without evaluating MSBuild.
/// </summary>
public sealed class SynchronizedWorkspaceLoader : WorkspaceLoader
{
    private const string DefaultGlobalUsings = """
        global using System;
        global using System.Collections.Generic;
        global using System.IO;
        global using System.Linq;
        global using System.Net.Http;
        global using System.Threading;
        global using System.Threading.Tasks;
        """;
    private readonly IReadOnlyList<string> _referencePaths;
    private readonly LooseFileWorkspaceLoader _looseFileLoader;

    /// <summary>
    /// Creates a synchronized workspace loader with the reference assemblies available in the browser.
    /// </summary>
    /// <param name="referencePaths">The portable executable reference paths.</param>
    public SynchronizedWorkspaceLoader(IReadOnlyList<string> referencePaths)
    {
        ArgumentNullException.ThrowIfNull(referencePaths);
        _referencePaths =
        [
            .. referencePaths
                .Select(Path.GetFullPath)
                .Distinct(PathComparer)
        ];
        _looseFileLoader = new LooseFileWorkspaceLoader(_referencePaths);
    }

    /// <summary>
    /// Completes restore because the browser host cannot run an external project system.
    /// </summary>
    /// <param name="rootPaths">The current absolute workspace roots.</param>
    /// <param name="cancellationToken">The restore cancellation token.</param>
    /// <returns>Zero because synchronized projects cannot restore in the browser.</returns>
    public override Task<int> RestoreAsync(
        IReadOnlyList<string> rootPaths,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(rootPaths);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(0);
    }

    /// <summary>
    /// Loads synchronized solutions, projects, file-based apps, and loose folders into Roslyn.
    /// </summary>
    /// <param name="rootPaths">The absolute synchronized workspace roots.</param>
    /// <param name="buildConfiguration">The configuration retained for host parity.</param>
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
        var snapshots = new List<WorkspaceFolderSnapshot>();
        try
        {
            foreach (string requestedRoot in rootPaths.Distinct(PathComparer))
            {
                cancellationToken.ThrowIfCancellationRequested();
                string rootPath = Path.GetFullPath(requestedRoot);
                IReadOnlyList<string> workspaceFiles = DiscoverWorkspaceFiles(
                    rootPath,
                    cancellationToken);
                if (workspaceFiles.Count == 0)
                {
                    snapshots.AddRange(await _looseFileLoader.LoadAsync(
                        [rootPath],
                        buildConfiguration,
                        progress: null,
                        cancellationToken).ConfigureAwait(false));
                    continue;
                }

                foreach (string workspaceFile in workspaceFiles)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    snapshots.Add(IsFileBasedApp(workspaceFile)
                        ? LoadFileBasedApp(rootPath, workspaceFile, cancellationToken)
                        : LoadProjectWorkspace(rootPath, workspaceFile, cancellationToken));
                }
            }

            ReportProgress(snapshots, progress);
            return snapshots;
        }
        catch
        {
            Dispose(snapshots);
            throw;
        }
    }

    private WorkspaceFolderSnapshot LoadFileBasedApp(
        string rootPath,
        string entryPointPath,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var workspace = new AdhocWorkspace();
        try
        {
            var projectId = ProjectId.CreateNewId(debugName: entryPointPath);
            var projectInfo = ProjectInfo.Create(
                projectId,
                VersionStamp.Create(),
                Path.GetFileName(entryPointPath),
                Path.GetFileName(entryPointPath),
                LanguageNames.CSharp,
                filePath: entryPointPath,
                parseOptions: CreateParseOptions(fileBasedApp: true),
                compilationOptions: CreateCompilationOptions(OutputKind.ConsoleApplication),
                metadataReferences: GetMetadataReferences());
            Solution solution = workspace.CurrentSolution.AddProject(projectInfo);
            solution = AddImplicitUsings(solution, projectId);
            solution = AddDocument(solution, projectId, entryPointPath);
            return Apply(rootPath, workspace, solution);
        }
        catch
        {
            workspace.Dispose();
            throw;
        }
    }

    private WorkspaceFolderSnapshot LoadProjectWorkspace(
        string rootPath,
        string workspaceFile,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<string> initialProjectPaths = workspaceFile.EndsWith(
            ".csproj",
            StringComparison.OrdinalIgnoreCase)
            ? [Path.GetFullPath(workspaceFile)]
            : SolutionProjectCounter.ReadCSharpProjectPaths(workspaceFile);
        string[] projectPaths = DiscoverProjectGraph(initialProjectPaths, cancellationToken);
        var workspace = new AdhocWorkspace();
        try
        {
            Dictionary<string, ProjectId> projectIds = projectPaths.ToDictionary(
                static path => path,
                static path => ProjectId.CreateNewId(debugName: path),
                PathComparer);
            Solution solution = workspace.CurrentSolution;
            foreach (string projectPath in projectPaths)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ProjectId projectId = projectIds[projectPath];
                XDocument project = LoadProject(projectPath);
                string projectName = Path.GetFileNameWithoutExtension(projectPath);
                ProjectReference[] projectReferences =
                [
                    .. ReadProjectReferencePaths(projectPath, project)
                        .Where(projectIds.ContainsKey)
                        .Select(path => new ProjectReference(projectIds[path]))
                ];
                var projectInfo = ProjectInfo.Create(
                    projectId,
                    VersionStamp.Create(),
                    projectName,
                    projectName,
                    LanguageNames.CSharp,
                    filePath: projectPath,
                    outputFilePath: null,
                    compilationOptions: CreateCompilationOptions(ReadOutputKind(project)),
                    parseOptions: CreateParseOptions(fileBasedApp: false),
                    projectReferences: projectReferences,
                    metadataReferences: GetMetadataReferences());
                solution = solution.AddProject(projectInfo);
                if (IsImplicitUsingsEnabled(project))
                {
                    solution = AddImplicitUsings(solution, projectId);
                }

                foreach (string documentPath in ReadCompilePaths(projectPath, project))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    solution = AddDocument(solution, projectId, documentPath);
                }
            }

            return Apply(rootPath, workspace, solution);
        }
        catch
        {
            workspace.Dispose();
            throw;
        }
    }

    private static WorkspaceFolderSnapshot Apply(
        string rootPath,
        Workspace workspace,
        Solution solution)
    {
        if (!workspace.TryApplyChanges(solution))
        {
            throw new InvalidOperationException(
                $"Roslyn rejected the synchronized workspace under {rootPath}.");
        }

        return new WorkspaceFolderSnapshot
        {
            RootPath = rootPath,
            Workspace = workspace,
            Solution = workspace.CurrentSolution
        };
    }

    private static string[] DiscoverProjectGraph(
        IReadOnlyList<string> initialProjectPaths,
        CancellationToken cancellationToken)
    {
        var projectPaths = new List<string>();
        var discovered = new HashSet<string>(PathComparer);
        var pending = new Queue<string>(initialProjectPaths.Select(Path.GetFullPath));
        while (pending.TryDequeue(out string? projectPath))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!discovered.Add(projectPath))
            {
                continue;
            }

            if (!File.Exists(projectPath))
            {
                throw new FileNotFoundException(
                    $"Synchronized project does not exist: {projectPath}",
                    projectPath);
            }

            projectPaths.Add(projectPath);
            XDocument project = LoadProject(projectPath);
            foreach (string referencePath in ReadProjectReferencePaths(projectPath, project))
            {
                pending.Enqueue(referencePath);
            }
        }

        return [.. projectPaths];
    }

    private static XDocument LoadProject(string projectPath)
    {
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null
        };
        using var reader = XmlReader.Create(projectPath, settings);
        return XDocument.Load(reader, LoadOptions.None);
    }

    private static IEnumerable<string> ReadProjectReferencePaths(
        string projectPath,
        XDocument project)
    {
        string projectDirectory = Path.GetDirectoryName(projectPath)
            ?? throw new InvalidDataException($"Project path has no parent: {projectPath}");
        return project
            .Descendants()
            .Where(static element =>
                element.Name.LocalName.Equals("ProjectReference", StringComparison.Ordinal))
            .SelectMany(static element => SplitItems(element.Attribute("Include")?.Value))
            .Select(path => ResolvePath(projectDirectory, path))
            .Distinct(PathComparer);
    }

    private static IReadOnlyList<string> ReadCompilePaths(
        string projectPath,
        XDocument project)
    {
        string projectDirectory = Path.GetDirectoryName(projectPath)
            ?? throw new InvalidDataException($"Project path has no parent: {projectPath}");
        bool defaultCompileItems = !string.Equals(
            ReadProperty(project, "EnableDefaultCompileItems"),
            "false",
            StringComparison.OrdinalIgnoreCase);
        var paths = new HashSet<string>(PathComparer);
        if (defaultCompileItems)
        {
            foreach (string path in Directory
                .EnumerateFiles(
                    projectDirectory,
                    "*.cs",
                    SearchOption.AllDirectories)
                .Where(path => !WorkspaceDiscovery.IsExcludedPath(
                    projectDirectory,
                    path)))
            {
                paths.Add(Path.GetFullPath(path));
            }
        }

        foreach (XElement compile in project.Descendants().Where(static element =>
            element.Name.LocalName.Equals("Compile", StringComparison.Ordinal)))
        {
            foreach (string include in SplitItems(compile.Attribute("Include")?.Value))
            {
                AddMatchingPaths(paths, projectDirectory, include);
            }

            foreach (string remove in SplitItems(compile.Attribute("Remove")?.Value))
            {
                RemoveMatchingPaths(paths, projectDirectory, remove);
            }
        }

        return [.. paths.Order(PathComparer)];
    }

    private static void AddMatchingPaths(
        HashSet<string> paths,
        string projectDirectory,
        string include)
    {
        if (!ContainsWildcard(include))
        {
            string path = ResolvePath(projectDirectory, include);
            if (File.Exists(path))
            {
                paths.Add(path);
            }

            return;
        }

        string searchRoot = GetSearchRoot(projectDirectory, include);
        if (!Directory.Exists(searchRoot))
        {
            return;
        }

        foreach (string path in Directory.EnumerateFiles(
            searchRoot,
            "*.cs",
            SearchOption.AllDirectories))
        {
            string relativePath = NormalizeRelativePath(
                Path.GetRelativePath(projectDirectory, path));
            if (MatchesGlob(NormalizeRelativePath(include), relativePath))
            {
                paths.Add(Path.GetFullPath(path));
            }
        }
    }

    private static void RemoveMatchingPaths(
        HashSet<string> paths,
        string projectDirectory,
        string remove)
    {
        if (!ContainsWildcard(remove))
        {
            paths.Remove(ResolvePath(projectDirectory, remove));
            return;
        }

        string normalizedRemove = NormalizeRelativePath(remove);
        paths.RemoveWhere(path => MatchesGlob(
            normalizedRemove,
            NormalizeRelativePath(Path.GetRelativePath(projectDirectory, path))));
    }

    private static string GetSearchRoot(string projectDirectory, string include)
    {
        string normalized = NormalizeRelativePath(include);
        int wildcardIndex = normalized.IndexOfAny(['*', '?']);
        int separatorIndex = normalized.LastIndexOf('/', wildcardIndex);
        string prefix = separatorIndex < 0 ? string.Empty : normalized[..separatorIndex];
        return ResolvePath(projectDirectory, prefix.Length == 0 ? "." : prefix);
    }

    private static bool MatchesGlob(string pattern, string path)
    {
        string[] patternSegments = pattern.Split('/', StringSplitOptions.RemoveEmptyEntries);
        string[] pathSegments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        bool[][] matches = CreateMatchTable(
            patternSegments.Length + 1,
            pathSegments.Length + 1);
        matches[0][0] = true;
        for (int patternIndex = 0; patternIndex < patternSegments.Length; patternIndex++)
        {
            string patternSegment = patternSegments[patternIndex];
            for (int pathIndex = 0; pathIndex <= pathSegments.Length; pathIndex++)
            {
                if (!matches[patternIndex][pathIndex])
                {
                    continue;
                }

                if (patternSegment.Equals("**", StringComparison.Ordinal))
                {
                    matches[patternIndex + 1][pathIndex] = true;
                    if (pathIndex < pathSegments.Length)
                    {
                        matches[patternIndex][pathIndex + 1] = true;
                    }
                }
                else if (pathIndex < pathSegments.Length &&
                    MatchesSegment(patternSegment, pathSegments[pathIndex]))
                {
                    matches[patternIndex + 1][pathIndex + 1] = true;
                }
            }
        }

        return matches[patternSegments.Length][pathSegments.Length];
    }

    private static bool MatchesSegment(string pattern, string value)
    {
        bool[][] matches = CreateMatchTable(pattern.Length + 1, value.Length + 1);
        matches[0][0] = true;
        for (int patternIndex = 0; patternIndex < pattern.Length; patternIndex++)
        {
            for (int valueIndex = 0; valueIndex <= value.Length; valueIndex++)
            {
                if (!matches[patternIndex][valueIndex])
                {
                    continue;
                }

                char patternCharacter = pattern[patternIndex];
                if (patternCharacter == '*')
                {
                    matches[patternIndex + 1][valueIndex] = true;
                    if (valueIndex < value.Length)
                    {
                        matches[patternIndex][valueIndex + 1] = true;
                    }
                }
                else if (valueIndex < value.Length &&
                    (patternCharacter == '?' || CharactersEqual(
                        patternCharacter,
                        value[valueIndex])))
                {
                    matches[patternIndex + 1][valueIndex + 1] = true;
                }
            }
        }

        return matches[pattern.Length][value.Length];
    }

    private static bool CharactersEqual(char left, char right) =>
        OperatingSystem.IsWindows()
            ? char.ToUpperInvariant(left) == char.ToUpperInvariant(right)
            : left == right;

    private static bool[][] CreateMatchTable(int rows, int columns)
    {
        bool[][] matches = new bool[rows][];
        for (int index = 0; index < rows; index++)
        {
            matches[index] = new bool[columns];
        }

        return matches;
    }

    private static bool ContainsWildcard(string value) =>
        value.Contains('*', StringComparison.Ordinal) ||
        value.Contains('?', StringComparison.Ordinal);

    private static string ResolvePath(string rootPath, string relativePath) =>
        Path.GetFullPath(
            relativePath
                .Replace('\\', Path.DirectorySeparatorChar)
                .Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar),
            rootPath);

    private static string NormalizeRelativePath(string path) =>
        path.Replace('\\', '/').Replace(Path.DirectorySeparatorChar, '/');

    private static string[] SplitItems(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? []
            : value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static string? ReadProperty(XDocument project, string propertyName) =>
        project
            .Descendants()
            .LastOrDefault(element => element.Name.LocalName.Equals(
                propertyName,
                StringComparison.OrdinalIgnoreCase))?
            .Value
            .Trim();

    private static bool IsImplicitUsingsEnabled(XDocument project) =>
        ReadProperty(project, "ImplicitUsings") is string value &&
        (value.Equals("enable", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("true", StringComparison.OrdinalIgnoreCase));

    private static OutputKind ReadOutputKind(XDocument project) =>
        ReadProperty(project, "OutputType")?.ToUpperInvariant() switch
        {
            "EXE" => OutputKind.ConsoleApplication,
            "WINEXE" => OutputKind.WindowsApplication,
            _ => OutputKind.DynamicallyLinkedLibrary
        };

    private static CSharpParseOptions CreateParseOptions(bool fileBasedApp) =>
        new CSharpParseOptions(LanguageVersion.CSharp14).WithFeatures(fileBasedApp
            ? [new KeyValuePair<string, string>("FileBasedProgram", "true")]
            : []);

    private static CSharpCompilationOptions CreateCompilationOptions(OutputKind outputKind) =>
        new(outputKind, nullableContextOptions: NullableContextOptions.Enable);

    private IEnumerable<MetadataReference> GetMetadataReferences() =>
        _referencePaths.Select(static path => MetadataReference.CreateFromFile(path));

    private static Solution AddImplicitUsings(Solution solution, ProjectId projectId) =>
        solution.AddDocument(
            DocumentId.CreateNewId(projectId, debugName: "Csls.ImplicitUsings.g.cs"),
            "Csls.ImplicitUsings.g.cs",
            SourceText.From(DefaultGlobalUsings, Encoding.UTF8));

    private static Solution AddDocument(
        Solution solution,
        ProjectId projectId,
        string path) =>
        solution.AddDocument(
            DocumentId.CreateNewId(projectId, debugName: path),
            Path.GetFileName(path),
            SourceText.From(File.ReadAllText(path), Encoding.UTF8),
            filePath: path);

    private static void ReportProgress(
        IReadOnlyList<WorkspaceFolderSnapshot> snapshots,
        IProgress<WorkspaceLoadProgress>? progress)
    {
        if (progress is null)
        {
            return;
        }

        Project[] projects =
        [
            .. snapshots
                .SelectMany(static snapshot => snapshot.Solution.Projects)
                .OrderBy(static project => project.FilePath ?? project.Name, PathComparer)
        ];
        for (int index = 0; index < projects.Length; index++)
        {
            progress.Report(new WorkspaceLoadProgress
            {
                ProjectName = projects[index].Name,
                CompletedProjects = index + 1,
                TotalProjects = projects.Length,
                Percentage = checked((index + 1) * 100 / Math.Max(1, projects.Length))
            });
        }
    }

    private static void Dispose(IEnumerable<WorkspaceFolderSnapshot> snapshots)
    {
        foreach (WorkspaceFolderSnapshot snapshot in snapshots)
        {
            snapshot.Workspace.Dispose();
        }
    }

    private static StringComparer PathComparer =>
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
}
