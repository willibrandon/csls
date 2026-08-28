using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;
using System.Text;

namespace Csls.Workspaces;

/// <summary>
/// Loads synchronized source files without an MSBuild process or desktop project system.
/// </summary>
public sealed class LooseFileWorkspaceLoader : WorkspaceLoader
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

    /// <summary>
    /// Creates a loose-file loader with the runtime assemblies available to compilations.
    /// </summary>
    /// <param name="referencePaths">The portable executable reference paths.</param>
    public LooseFileWorkspaceLoader(IReadOnlyList<string> referencePaths)
    {
        ArgumentNullException.ThrowIfNull(referencePaths);
        _referencePaths = [.. referencePaths.Select(Path.GetFullPath)];
    }

    /// <summary>
    /// Completes restore because synchronized loose files have no external project system.
    /// </summary>
    /// <param name="rootPaths">The current absolute workspace roots.</param>
    /// <param name="cancellationToken">The restore cancellation token.</param>
    /// <returns>Zero because no entry points require restoration.</returns>
    public override Task<int> RestoreAsync(
        IReadOnlyList<string> rootPaths,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(rootPaths);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(0);
    }

    /// <summary>
    /// Loads every root as an independent loose-file Roslyn project.
    /// </summary>
    /// <param name="rootPaths">The absolute workspace roots to load.</param>
    /// <param name="buildConfiguration">The unused build configuration.</param>
    /// <param name="progress">The optional ordered project progress destination.</param>
    /// <param name="cancellationToken">The operation cancellation token.</param>
    /// <returns>The ordered loaded workspace snapshots.</returns>
    public override Task<IReadOnlyList<WorkspaceFolderSnapshot>> LoadAsync(
        IReadOnlyList<string> rootPaths,
        string buildConfiguration,
        IProgress<WorkspaceLoadProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(rootPaths);
        ArgumentException.ThrowIfNullOrWhiteSpace(buildConfiguration);
        string[] distinctRootPaths =
        [
            .. rootPaths
                .Select(Path.GetFullPath)
                .Distinct(PathComparer)
        ];
        var snapshots = new List<WorkspaceFolderSnapshot>(distinctRootPaths.Length);
        try
        {
            foreach (string rootPath in distinctRootPaths)
            {
                cancellationToken.ThrowIfCancellationRequested();
                WorkspaceFolderSnapshot snapshot = LoadWithReferences(
                    rootPath,
                    cancellationToken);
                snapshots.Add(snapshot);
                progress?.Report(new WorkspaceLoadProgress
                {
                    ProjectName = snapshot.Solution.Projects.Single().Name,
                    CompletedProjects = snapshots.Count,
                    TotalProjects = distinctRootPaths.Length,
                    Percentage = checked(
                        snapshots.Count * 100 / Math.Max(1, distinctRootPaths.Length))
                });
            }

            return Task.FromResult<IReadOnlyList<WorkspaceFolderSnapshot>>(snapshots);
        }
        catch
        {
            Dispose(snapshots);
            throw;
        }
    }

    /// <summary>
    /// Loads one absolute source file or directory as a loose-file Roslyn project.
    /// </summary>
    /// <param name="rootPath">The absolute source file or directory.</param>
    /// <param name="cancellationToken">The load cancellation token.</param>
    /// <returns>The loaded workspace snapshot owned by the caller.</returns>
    internal static WorkspaceFolderSnapshot Load(
        string rootPath,
        CancellationToken cancellationToken)
    {
        string trustedPlatformAssemblies = AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")
            as string
            ?? throw new InvalidOperationException(
                "The .NET host did not provide its trusted platform assembly set.");
        string[] referencePaths = trustedPlatformAssemblies.Split(
            Path.PathSeparator,
            StringSplitOptions.RemoveEmptyEntries);
        return LoadCore(rootPath, referencePaths, cancellationToken);
    }

    private WorkspaceFolderSnapshot LoadWithReferences(
        string rootPath,
        CancellationToken cancellationToken) =>
        LoadCore(rootPath, _referencePaths, cancellationToken);

    private static WorkspaceFolderSnapshot LoadCore(
        string rootPath,
        IReadOnlyList<string> referencePaths,
        CancellationToken cancellationToken)
    {
        bool isSourceFile = File.Exists(rootPath);
        string projectName = isSourceFile
            ? Path.GetFileNameWithoutExtension(rootPath)
            : new DirectoryInfo(rootPath).Name;
        var workspace = new AdhocWorkspace();
        try
        {
            var projectId = ProjectId.CreateNewId(debugName: rootPath);
            var projectInfo = ProjectInfo.Create(
                projectId,
                VersionStamp.Create(),
                projectName,
                projectName,
                LanguageNames.CSharp,
                filePath: rootPath,
                parseOptions: new CSharpParseOptions(LanguageVersion.CSharp14),
                compilationOptions: new CSharpCompilationOptions(
                    OutputKind.DynamicallyLinkedLibrary),
                metadataReferences: GetTrustedPlatformReferences(referencePaths));
            Solution solution = workspace.CurrentSolution.AddProject(projectInfo);
            solution = solution.AddDocument(
                DocumentId.CreateNewId(projectId, debugName: "Csls.ImplicitUsings.g.cs"),
                "Csls.ImplicitUsings.g.cs",
                SourceText.From(DefaultGlobalUsings, Encoding.UTF8));
            IEnumerable<string> sourceFiles = isSourceFile
                ? [rootPath]
                : Directory
                    .EnumerateFiles(rootPath, "*.cs", SearchOption.TopDirectoryOnly)
                    .Order(StringComparer.Ordinal);
            foreach (string path in sourceFiles)
            {
                cancellationToken.ThrowIfCancellationRequested();
                solution = solution.AddDocument(
                    DocumentId.CreateNewId(projectId, debugName: path),
                    Path.GetFileName(path),
                    SourceText.From(File.ReadAllText(path), Encoding.UTF8),
                    filePath: path);
            }

            if (!workspace.TryApplyChanges(solution))
            {
                throw new InvalidOperationException(
                    $"Roslyn rejected loose files under {rootPath}.");
            }

            return new WorkspaceFolderSnapshot
            {
                RootPath = rootPath,
                Workspace = workspace,
                Solution = workspace.CurrentSolution
            };
        }
        catch
        {
            workspace.Dispose();
            throw;
        }
    }

    private static IEnumerable<MetadataReference> GetTrustedPlatformReferences(
        IReadOnlyList<string> referencePaths)
    {
        return referencePaths
            .Distinct(PathComparer)
            .Select(static path => MetadataReference.CreateFromFile(path));
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
