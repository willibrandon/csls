using Microsoft.Build.Execution;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Host.Mef;
using System.Collections.Immutable;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using System.Text;

namespace Csls.Workspaces;

/// <summary>
/// Converts completed MSBuild design-time states into one fully linked Roslyn workspace.
/// </summary>
internal static class MSBuildProjectInfoFactory
{
    private static readonly RoslynAnalyzerAssemblyLoader s_analyzerLoader = new(
        AssemblyLoadContext.GetLoadContext(typeof(MSBuildProjectInfoFactory).Assembly) ??
        AssemblyLoadContext.Default);

    /// <summary>
    /// Creates a Roslyn workspace from completed design-time project states.
    /// </summary>
    /// <param name="solutionPath">The solution path, or the first project path.</param>
    /// <param name="snapshots">The completed project states.</param>
    /// <param name="reportDiagnostic">The workspace diagnostic destination.</param>
    /// <returns>The workspace and its current solution.</returns>
    internal static (Workspace Workspace, Solution Solution) Create(
        string solutionPath,
        IReadOnlyList<MSBuildProjectSnapshot> snapshots,
        Action<WorkspaceDiagnosticKind, string> reportDiagnostic)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(solutionPath);
        ArgumentNullException.ThrowIfNull(snapshots);
        ArgumentNullException.ThrowIfNull(reportDiagnostic);
        var workspace = new AdhocWorkspace(MefHostServices.DefaultHost, WorkspaceKind.MSBuild);
        try
        {
            var projectIds = snapshots.ToDictionary(
                static snapshot => snapshot,
                static snapshot => ProjectId.CreateNewId(debugName: snapshot.ProjectPath));
            var snapshotsByPath = snapshots
                .GroupBy(static snapshot => snapshot.ProjectPath, PathComparer)
                .ToDictionary(
                    static group => group.Key,
                    static group => group.ToArray(),
                    PathComparer);
            var knownProjectOutputPaths = snapshots
                .SelectMany(static snapshot => GetOutputPaths(
                    snapshot.ProjectInstance,
                    snapshot.ProjectPath))
                .ToHashSet(PathComparer);
            RoslynAnalyzerAssemblyLoader analyzerLoader = CreateAnalyzerLoader(snapshots);
            ProjectInfo[] projectInfos =
            [
                .. snapshots.Select(snapshot => CreateProjectInfo(
                    snapshot,
                    projectIds,
                    snapshotsByPath,
                    knownProjectOutputPaths,
                    analyzerLoader,
                    reportDiagnostic))
            ];
            var solutionInfo = SolutionInfo.Create(
                SolutionId.CreateNewId(debugName: solutionPath),
                VersionStamp.Create(),
                filePath: solutionPath,
                projects: projectInfos);
            workspace.AddSolution(solutionInfo);
            return (workspace, workspace.CurrentSolution);
        }
        catch
        {
            workspace.Dispose();
            throw;
        }
    }

    private static ProjectInfo CreateProjectInfo(
        MSBuildProjectSnapshot snapshot,
        Dictionary<MSBuildProjectSnapshot, ProjectId> projectIds,
        Dictionary<string, MSBuildProjectSnapshot[]> snapshotsByPath,
        IReadOnlySet<string> knownProjectOutputPaths,
        RoslynAnalyzerAssemblyLoader analyzerLoader,
        Action<WorkspaceDiagnosticKind, string> reportDiagnostic)
    {
        ProjectInstance project = snapshot.ProjectInstance;
        string projectPath = snapshot.ProjectPath;
        string projectDirectory = Path.GetDirectoryName(projectPath)
            ?? throw new InvalidDataException($"Project path has no parent directory: {projectPath}");
        string[] commandLineArguments =
        [
            .. project.GetItems("CscCommandLineArgs").Select(static item => item.EvaluatedInclude)
        ];
        CSharpCommandLineArguments parsedArguments = CSharpCommandLineParser.Default.Parse(
            commandLineArguments,
            projectDirectory,
            RuntimeEnvironment.GetRuntimeDirectory());
        ParseOptions parseOptions = parsedArguments.ParseOptions.DocumentationMode ==
            DocumentationMode.None
            ? parsedArguments.ParseOptions.WithDocumentationMode(DocumentationMode.Parse)
            : parsedArguments.ParseOptions;
        CompilationOptions compilationOptions = parsedArguments.CompilationOptions
            .WithXmlReferenceResolver(new XmlFileResolver(projectDirectory))
            .WithSourceReferenceResolver(new SourceFileResolver([], projectDirectory))
            .WithStrongNameProvider(new DesktopStrongNameProvider(
                parsedArguments.KeyFileSearchPaths))
            .WithAssemblyIdentityComparer(DesktopAssemblyIdentityComparer.Default);
        List<PortableExecutableReference> metadataReferences = CreateMetadataReferences(
            parsedArguments,
            project,
            projectDirectory,
            projectPath,
            knownProjectOutputPaths,
            reportDiagnostic);
        var metadataPaths = metadataReferences
            .Select(static reference => reference.FilePath)
            .OfType<string>()
            .ToHashSet(PathComparer);
        List<ProjectReference> projectReferences = CreateProjectReferences(
            snapshot,
            projectIds,
            snapshotsByPath,
            metadataPaths);
        var referencedProjectIds = projectReferences
            .Select(static reference => reference.ProjectId)
            .ToHashSet(EqualityComparer<ProjectId>.Default);
        var projectOutputPaths = projectIds
            .Where(pair => referencedProjectIds.Contains(pair.Value))
            .SelectMany(static pair => GetOutputPaths(
                pair.Key.ProjectInstance,
                pair.Key.ProjectPath))
            .ToHashSet(PathComparer);
        metadataReferences.RemoveAll(reference =>
            reference.FilePath is string path && projectOutputPaths.Contains(path));

        ProjectId projectId = projectIds[snapshot];
        DocumentInfo[] documents = CreateDocuments(
            project.GetItems("Compile"),
            projectId,
            projectDirectory,
            parsedArguments.Encoding,
            reportDiagnostic,
            projectPath);
        DocumentInfo[] additionalDocuments = CreateDocuments(
            project.GetItems("AdditionalFiles"),
            projectId,
            projectDirectory,
            parsedArguments.Encoding,
            reportDiagnostic,
            projectPath);
        DocumentInfo[] analyzerConfigDocuments = CreateDocuments(
            project.GetItems("EditorConfigFiles"),
            projectId,
            projectDirectory,
            Encoding.UTF8,
            reportDiagnostic,
            projectPath);
        AnalyzerReference[] analyzerReferences = CreateAnalyzerReferences(
            parsedArguments,
            project,
            projectDirectory,
            analyzerLoader);
        string? targetFramework = ReadProperty(project, "TargetFramework");
        bool multiTargeted = snapshotsByPath[projectPath].Length > 1;
        string projectName = Path.GetFileNameWithoutExtension(projectPath);
        if (multiTargeted && !string.IsNullOrWhiteSpace(targetFramework))
        {
            projectName = $"{projectName}({targetFramework})";
        }

        string? outputPath = ResolveProjectPath(
            ReadProperty(project, "TargetPath"),
            projectDirectory);
        string? outputRefPath = ResolveProjectPath(
            ReadProperty(project, "TargetRefPath"),
            projectDirectory);
        string? intermediateOutputPath = ResolveProjectPath(
            project.GetItems("IntermediateAssembly").FirstOrDefault()?.EvaluatedInclude,
            projectDirectory);
        string? generatedFilesPath = ResolveProjectPath(
            ReadProperty(project, "CompilerGeneratedFilesOutputPath"),
            projectDirectory);
        string assemblyName = parsedArguments.CompilationName ??
            ReadProperty(project, "AssemblyName") ??
            Path.GetFileNameWithoutExtension(projectPath);
        ProjectInfo info = ProjectInfo.Create(
            projectId,
            VersionStamp.Create(File.GetLastWriteTimeUtc(projectPath)),
            projectName,
            assemblyName,
            LanguageNames.CSharp,
            filePath: projectPath,
            outputFilePath: outputPath,
            compilationOptions: compilationOptions,
            parseOptions: parseOptions,
            documents: documents,
            projectReferences: projectReferences,
            metadataReferences: metadataReferences,
            analyzerReferences: analyzerReferences,
            additionalDocuments: additionalDocuments,
            outputRefFilePath: outputRefPath)
            .WithAnalyzerConfigDocuments(analyzerConfigDocuments)
            .WithDefaultNamespace(ReadProperty(project, "RootNamespace"));
        CompilationOutputInfo outputInfo = default(CompilationOutputInfo)
            .WithAssemblyPath(intermediateOutputPath)
            .WithGeneratedFilesOutputDirectory(generatedFilesPath);
        return info.WithCompilationOutputInfo(outputInfo);
    }

    private static List<PortableExecutableReference> CreateMetadataReferences(
        CSharpCommandLineArguments parsedArguments,
        ProjectInstance project,
        string projectDirectory,
        string projectPath,
        IReadOnlySet<string> knownProjectOutputPaths,
        Action<WorkspaceDiagnosticKind, string> reportDiagnostic)
    {
        var references = new List<PortableExecutableReference>();
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (CommandLineReference reference in parsedArguments.MetadataReferences)
        {
            string? path = ResolveReferencePath(
                reference.Reference,
                projectDirectory,
                parsedArguments.ReferencePaths);
            if (path is null)
            {
                string unresolvedPath = Path.IsPathFullyQualified(reference.Reference)
                    ? Path.GetFullPath(reference.Reference)
                    : Path.GetFullPath(reference.Reference, projectDirectory);
                if (!knownProjectOutputPaths.Contains(unresolvedPath))
                {
                    reportDiagnostic(
                        WorkspaceDiagnosticKind.Warning,
                        $"Unable to resolve metadata reference {reference.Reference} for " +
                        $"{projectPath}.");
                }

                continue;
            }

            AddReference(path, reference.Properties);
        }

        if (references.Count == 0)
        {
            foreach (ProjectItemInstance item in project.GetItems("ReferencePath"))
            {
                string? path = ResolveReferencePath(
                    item.EvaluatedInclude,
                    projectDirectory,
                    []);
                if (path is not null)
                {
                    AddReference(
                        path,
                        new MetadataReferenceProperties(
                            aliases: ParseAliases(item.GetMetadataValue("Aliases"))));
                }
            }
        }

        return references;

        void AddReference(string path, MetadataReferenceProperties properties)
        {
            string key = $"{path}\0{properties.EmbedInteropTypes}\0{string.Join(',', properties.Aliases)}";
            if (keys.Add(key))
            {
                references.Add(MetadataReference.CreateFromFile(path, properties));
            }
        }
    }

    private static List<ProjectReference> CreateProjectReferences(
        MSBuildProjectSnapshot source,
        Dictionary<MSBuildProjectSnapshot, ProjectId> projectIds,
        Dictionary<string, MSBuildProjectSnapshot[]> snapshotsByPath,
        IReadOnlySet<string> metadataPaths)
    {
        var references = new List<ProjectReference>();
        string projectDirectory = Path.GetDirectoryName(source.ProjectPath)!;
        foreach (ProjectItemInstance item in source.ProjectInstance.GetItems("ProjectReference"))
        {
            if (string.Equals(
                item.GetMetadataValue("ReferenceOutputAssembly"),
                bool.FalseString,
                StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string referencePath = Path.GetFullPath(
                NormalizePath(item.EvaluatedInclude),
                projectDirectory);
            if (!snapshotsByPath.TryGetValue(
                referencePath,
                out MSBuildProjectSnapshot[]? candidates) ||
                candidates.Length == 0)
            {
                continue;
            }

            MSBuildProjectSnapshot target = candidates.FirstOrDefault(candidate =>
                GetOutputPaths(candidate.ProjectInstance, candidate.ProjectPath)
                    .Any(metadataPaths.Contains)) ??
                candidates.FirstOrDefault(candidate => string.Equals(
                    ReadProperty(candidate.ProjectInstance, "TargetFramework"),
                    ReadProperty(source.ProjectInstance, "TargetFramework"),
                    StringComparison.OrdinalIgnoreCase)) ??
                candidates[0];
            references.Add(new ProjectReference(
                projectIds[target],
                aliases: ParseAliases(item.GetMetadataValue("Aliases"))));
        }

        return references;
    }

    private static DocumentInfo[] CreateDocuments(
        ICollection<ProjectItemInstance> items,
        ProjectId projectId,
        string projectDirectory,
        Encoding? encoding,
        Action<WorkspaceDiagnosticKind, string> reportDiagnostic,
        string projectPath)
    {
        var documents = new List<DocumentInfo>(items.Count);
        var paths = new HashSet<string>(PathComparer);
        foreach (ProjectItemInstance item in items)
        {
            string path = Path.GetFullPath(NormalizePath(item.EvaluatedInclude), projectDirectory);
            if (!paths.Add(path))
            {
                reportDiagnostic(
                    WorkspaceDiagnosticKind.Warning,
                    $"Duplicate source file {path} in project {projectPath}.");
                continue;
            }

            string logicalPath = item.GetMetadataValue("Link");
            if (string.IsNullOrWhiteSpace(logicalPath))
            {
                logicalPath = Path.GetRelativePath(projectDirectory, path);
            }

            string[] parts = NormalizePath(logicalPath).Split(
                Path.DirectorySeparatorChar,
                StringSplitOptions.RemoveEmptyEntries);
            string name = parts.Length == 0 ? Path.GetFileName(path) : parts[^1];
            string[] folders = parts.Length <= 1 ? [] : parts[..^1];
            documents.Add(DocumentInfo.Create(
                DocumentId.CreateNewId(projectId, debugName: path),
                name,
                folders,
                SourceCodeKind.Regular,
                new FileTextLoader(path, encoding),
                filePath: path));
        }

        return [.. documents];
    }

    private static AnalyzerReference[] CreateAnalyzerReferences(
        CSharpCommandLineArguments parsedArguments,
        ProjectInstance project,
        string projectDirectory,
        RoslynAnalyzerAssemblyLoader analyzerLoader)
    {
        string[] paths =
        [
            .. parsedArguments.AnalyzerReferences
            .Select(static reference => reference.FilePath)
            .Concat(project.GetItems("Analyzer").Select(static item => item.EvaluatedInclude))
            .Select(path => ResolveProjectPath(path, projectDirectory))
            .OfType<string>()
            .Where(File.Exists)
            .Distinct(PathComparer)
        ];
        foreach (string path in paths)
        {
            analyzerLoader.AddDependencyLocation(path);
        }

        return
        [
            .. paths.Select(path => new AnalyzerFileReference(path, analyzerLoader))
        ];
    }

    private static RoslynAnalyzerAssemblyLoader CreateAnalyzerLoader(
        IReadOnlyList<MSBuildProjectSnapshot> snapshots)
    {
        IEnumerable<string> analyzerPaths = snapshots
            .SelectMany(static snapshot => snapshot.ProjectInstance
                .GetItems("Analyzer")
                .Select(item => (snapshot.ProjectPath, item.EvaluatedInclude)))
            .Select(static analyzer => ResolveProjectPath(
                analyzer.EvaluatedInclude,
                Path.GetDirectoryName(analyzer.ProjectPath)!))
            .OfType<string>()
            .Where(File.Exists)
            .Distinct(PathComparer);
        foreach (string path in analyzerPaths)
        {
            s_analyzerLoader.AddDependencyLocation(path);
        }

        return s_analyzerLoader;
    }

    private static IEnumerable<string> GetOutputPaths(
        ProjectInstance project,
        string projectPath)
    {
        string projectDirectory = Path.GetDirectoryName(projectPath)!;
        string? outputPath = ResolveProjectPath(ReadProperty(project, "TargetPath"), projectDirectory);
        if (outputPath is not null)
        {
            yield return outputPath;
        }

        string? outputRefPath = ResolveProjectPath(
            ReadProperty(project, "TargetRefPath"),
            projectDirectory);
        if (outputRefPath is not null)
        {
            yield return outputRefPath;
        }
    }

    private static string? ResolveReferencePath(
        string reference,
        string projectDirectory,
        ImmutableArray<string> searchPaths)
    {
        if (Path.IsPathRooted(reference))
        {
            string fullPath = Path.GetFullPath(reference);
            return File.Exists(fullPath) ? fullPath : null;
        }

        string projectRelativePath = Path.GetFullPath(reference, projectDirectory);
        if (File.Exists(projectRelativePath))
        {
            return projectRelativePath;
        }

        return searchPaths
            .Select(searchPath => Path.GetFullPath(reference, searchPath))
            .FirstOrDefault(File.Exists);
    }

    private static string? ResolveProjectPath(string? path, string projectDirectory)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        return Path.GetFullPath(NormalizePath(path), projectDirectory);
    }

    private static ImmutableArray<string> ParseAliases(string aliases) =>
        string.IsNullOrWhiteSpace(aliases)
            ? []
            :
            [
                .. aliases.Split(
                    ',',
                    StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            ];

    private static string? ReadProperty(ProjectInstance project, string propertyName) =>
        project.GetProperty(propertyName)?.EvaluatedValue is { Length: > 0 } value
            ? value
            : null;

    private static string NormalizePath(string path) =>
        path.Replace('\\', Path.DirectorySeparatorChar)
            .Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);

    private static StringComparer PathComparer =>
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
}
