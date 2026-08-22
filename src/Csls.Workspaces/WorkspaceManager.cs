using System.Collections.Immutable;
using System.Text;
using Csls.Protocol;
using Microsoft.Build.Locator;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.MSBuild;
using Microsoft.CodeAnalysis.QuickInfo;
using Microsoft.CodeAnalysis.Text;
using Microsoft.Extensions.Logging;
using LspRange = Csls.Protocol.Range;

namespace Csls.Workspaces;

/// <summary>
/// Loads multi-root Roslyn workspaces and publishes immutable document snapshots.
/// </summary>
public sealed partial class WorkspaceManager : IAsyncDisposable
{
    private static readonly Lock s_msbuildRegistrationLock = new();
    private static bool s_msbuildRegistered;

    private readonly ILogger<WorkspaceManager> _logger;
    private readonly SemaphoreSlim _mutationGate = new(1, 1);
    private readonly Dictionary<string, int> _documentVersions = new(
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    private ImmutableArray<(string RootPath, Workspace Workspace, Solution Solution)> _folders = [];
    private long _generation;
    private int _disposeState;

    /// <summary>
    /// Initializes workspace management with structured diagnostics.
    /// </summary>
    /// <param name="logger">The workspace logger.</param>
    public WorkspaceManager(ILogger<WorkspaceManager> logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
    }

    /// <summary>
    /// Gets the current immutable workspace generation.
    /// </summary>
    public long Generation => Interlocked.Read(ref _generation);

    /// <summary>
    /// Gets the absolute roots in the current immutable workspace snapshot.
    /// </summary>
    public IReadOnlyList<string> WorkspaceRoots =>
        [.. _folders
            .Select(static folder => folder.RootPath)
            .Distinct(PathComparer)];

    /// <summary>
    /// Loads each workspace root using its solution, project, or loose C# files.
    /// </summary>
    /// <param name="rootPaths">Absolute workspace root paths.</param>
    /// <param name="cancellationToken">The operation cancellation token.</param>
    /// <returns>A task that completes after the new generation is published.</returns>
    public async Task LoadAsync(
        IReadOnlyList<string> rootPaths,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(rootPaths);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposeState) != 0, this);

        ImmutableArray<(string RootPath, Workspace Workspace, Solution Solution)>.Builder loadedFolders =
            ImmutableArray.CreateBuilder<(
            string RootPath,
            Workspace Workspace,
            Solution Solution)>(rootPaths.Count);
        try
        {
            foreach (string requestedRoot in rootPaths.Distinct(PathComparer))
            {
                cancellationToken.ThrowIfCancellationRequested();
                string rootPath = Path.GetFullPath(requestedRoot);
                IReadOnlyList<string> workspaceFiles = WorkspaceDiscovery.Discover(rootPath);
                if (workspaceFiles.Count == 0)
                {
                    (Workspace looseWorkspace, Solution looseSolution) = LoadLooseFiles(rootPath);
                    loadedFolders.Add((rootPath, looseWorkspace, looseSolution));
                    continue;
                }

                foreach (string workspaceFile in workspaceFiles)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    EnsureMsBuildRegistered();
                    var workspace = MSBuildWorkspace.Create();
                    workspace.RegisterWorkspaceFailedHandler(eventArgs =>
                        LogWorkspaceDiagnostic(
                            eventArgs.Diagnostic.Kind,
                            eventArgs.Diagnostic.Message));
                    loadedFolders.Add((rootPath, workspace, workspace.CurrentSolution));
                    int loadedFolderIndex = loadedFolders.Count - 1;
                    Solution solution;
                    if (workspaceFile.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
                    {
                        Project project = await workspace
                            .OpenProjectAsync(workspaceFile, cancellationToken: cancellationToken)
                            .ConfigureAwait(false);
                        solution = project.Solution;
                    }
                    else
                    {
                        solution = await workspace
                            .OpenSolutionAsync(workspaceFile, cancellationToken: cancellationToken)
                            .ConfigureAwait(false);
                    }

                    loadedFolders[loadedFolderIndex] = (rootPath, workspace, solution);
                    LogWorkspaceLoaded(
                        solution.ProjectIds.Count,
                        workspaceFile);
                }
            }

            await _mutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                ImmutableArray<(string RootPath, Workspace Workspace, Solution Solution)> previous = _folders;
                _folders = loadedFolders.MoveToImmutable();
                _documentVersions.Clear();
                Interlocked.Increment(ref _generation);
                DisposeFolders(previous);
            }
            finally
            {
                _mutationGate.Release();
            }
        }
        catch
        {
            DisposeFolders(loadedFolders.ToImmutable());
            throw;
        }
    }

    /// <summary>
    /// Applies a versioned complete-text overlay for an opened client document.
    /// </summary>
    /// <param name="document">The opened document.</param>
    /// <param name="cancellationToken">The operation cancellation token.</param>
    /// <returns>A task that completes after the mutation is published.</returns>
    public async Task OpenDocumentAsync(
        TextDocumentItem document,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(document);
        string path = document.Uri.GetFileSystemPath();
        await _mutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_documentVersions.TryGetValue(path, out int currentVersion) &&
                document.Version <= currentVersion)
            {
                throw new InvalidOperationException(
                    $"Document version {document.Version} does not follow version {currentVersion}.");
            }

            int folderIndex = FindFolderIndex(path, _folders);
            if (folderIndex < 0)
            {
                throw new InvalidOperationException($"No workspace folder owns document {path}.");
            }

            (string rootPath, Workspace workspace, Solution solution) = _folders[folderIndex];
            Document? roslynDocument = FindDocument(solution, path);
            if (roslynDocument is null)
            {
                Project project = solution.Projects.FirstOrDefault()
                    ?? throw new InvalidOperationException("The workspace contains no C# project.");
                var documentId = DocumentId.CreateNewId(project.Id, debugName: path);
                solution = solution.AddDocument(
                    documentId,
                    Path.GetFileName(path),
                    SourceText.From(document.Text, Encoding.UTF8),
                    filePath: path);
            }
            else
            {
                solution = solution.WithDocumentText(
                    roslynDocument.Id,
                    SourceText.From(document.Text, Encoding.UTF8),
                    PreservationMode.PreserveIdentity);
            }

            if (!workspace.TryApplyChanges(solution))
            {
                throw new InvalidOperationException($"Roslyn rejected the document overlay for {path}.");
            }

            _folders = _folders.SetItem(folderIndex, (rootPath, workspace, workspace.CurrentSolution));
            _documentVersions[path] = document.Version;
            Interlocked.Increment(ref _generation);
        }
        finally
        {
            _mutationGate.Release();
        }
    }

    /// <summary>
    /// Resolves hover information from the current immutable Roslyn snapshot.
    /// </summary>
    /// <param name="parameters">The target document position.</param>
    /// <param name="cancellationToken">The operation cancellation token.</param>
    /// <returns>Hover information, or null when no symbol is present.</returns>
    public async Task<Hover?> GetHoverAsync(
        TextDocumentPositionParams parameters,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        string path = parameters.TextDocument.Uri.GetFileSystemPath();
        ImmutableArray<(string RootPath, Workspace Workspace, Solution Solution)> folders = _folders;
        int folderIndex = FindFolderIndex(path, folders);
        if (folderIndex < 0)
        {
            return null;
        }

        Document? document = FindDocument(folders[folderIndex].Solution, path);
        if (document is null)
        {
            return null;
        }

        SourceText text = await document.GetTextAsync(cancellationToken).ConfigureAwait(false);
        int offset = GetOffset(text, parameters.Position);
        var quickInfoService = QuickInfoService.GetService(document);
        if (quickInfoService is not null)
        {
            QuickInfoItem? quickInfo = await quickInfoService
                .GetQuickInfoAsync(document, offset, cancellationToken)
                .ConfigureAwait(false);
            if (quickInfo is not null)
            {
                string markdown = QuickInfoMarkdownFormatter.Format(quickInfo);
                if (!string.IsNullOrWhiteSpace(markdown))
                {
                    LinePositionSpan quickInfoLineSpan = text.Lines.GetLinePositionSpan(
                        quickInfo.Span);
                    return new Hover
                    {
                        Contents = new MarkupContent
                        {
                            Kind = "markdown",
                            Value = markdown
                        },
                        Range = new LspRange(
                            new Position(
                                quickInfoLineSpan.Start.Line,
                                quickInfoLineSpan.Start.Character),
                            new Position(
                                quickInfoLineSpan.End.Line,
                                quickInfoLineSpan.End.Character))
                    };
                }
            }
        }

        SyntaxNode root = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Roslyn returned no syntax root.");
        SyntaxToken token = root.FindToken(offset, findInsideTrivia: true);
        SemanticModel semanticModel = await document
            .GetSemanticModelAsync(cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException("Roslyn returned no semantic model.");
        ISymbol? symbol = semanticModel.GetSymbolInfo(token.Parent!, cancellationToken).Symbol
            ?? semanticModel.GetDeclaredSymbol(token.Parent!, cancellationToken);
        if (symbol is null)
        {
            return null;
        }

        LinePositionSpan lineSpan = text.Lines.GetLinePositionSpan(token.Span);
        return new Hover
        {
            Contents = new MarkupContent
            {
                Kind = "markdown",
                Value = $"```csharp{Environment.NewLine}{symbol.ToDisplayString()}{Environment.NewLine}```"
            },
            Range = new LspRange(
                new Position(lineSpan.Start.Line, lineSpan.Start.Character),
                new Position(lineSpan.End.Line, lineSpan.End.Character))
        };
    }

    /// <summary>
    /// Disposes every Roslyn workspace and synchronization primitive.
    /// </summary>
    /// <returns>A completed value task after resources are released.</returns>
    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposeState, 1) == 0)
        {
            DisposeFolders(_folders);
            _folders = [];
            _mutationGate.Dispose();
            GC.SuppressFinalize(this);
        }

        return ValueTask.CompletedTask;
    }

    private static StringComparer PathComparer =>
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private static void EnsureMsBuildRegistered()
    {
        lock (s_msbuildRegistrationLock)
        {
            if (!s_msbuildRegistered)
            {
                MSBuildLocator.RegisterDefaults();
                s_msbuildRegistered = true;
            }
        }
    }

    private static (Workspace Workspace, Solution Solution) LoadLooseFiles(string rootPath)
    {
        bool isSourceFile = File.Exists(rootPath);
        string projectName = isSourceFile
            ? Path.GetFileNameWithoutExtension(rootPath)
            : new DirectoryInfo(rootPath).Name;
        var workspace = new AdhocWorkspace();
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
            metadataReferences: GetTrustedPlatformReferences());
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
            solution = solution.AddDocument(
                DocumentId.CreateNewId(projectId, debugName: path),
                Path.GetFileName(path),
                SourceText.From(File.ReadAllText(path), Encoding.UTF8),
                filePath: path);
        }

        if (!workspace.TryApplyChanges(solution))
        {
            workspace.Dispose();
            throw new InvalidOperationException($"Roslyn rejected loose files under {rootPath}.");
        }

        return (workspace, workspace.CurrentSolution);
    }

    private const string DefaultGlobalUsings = """
        global using System;
        global using System.Collections.Generic;
        global using System.IO;
        global using System.Linq;
        global using System.Net.Http;
        global using System.Threading;
        global using System.Threading.Tasks;
        """;

    private static IEnumerable<MetadataReference> GetTrustedPlatformReferences()
    {
        string trustedPlatformAssemblies = AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")
            as string
            ?? throw new InvalidOperationException(
                "The .NET host did not provide its trusted platform assembly set.");
        return trustedPlatformAssemblies
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Distinct(PathComparer)
            .Select(static path => MetadataReference.CreateFromFile(path));
    }

    private static int FindFolderIndex(
        string documentPath,
        ImmutableArray<(string RootPath, Workspace Workspace, Solution Solution)> folders)
    {
        int bestIndex = -1;
        int bestLength = -1;
        bool bestContainsDocument = false;
        for (int index = 0; index < folders.Length; index++)
        {
            string rootPath = Path.TrimEndingDirectorySeparator(folders[index].RootPath);
            bool ownsPath = string.Equals(documentPath, rootPath, PathComparison) ||
                documentPath.StartsWith(
                    rootPath + Path.DirectorySeparatorChar,
                    PathComparison);
            if (!ownsPath)
            {
                continue;
            }

            bool containsDocument = FindDocument(folders[index].Solution, documentPath) is not null;
            if ((containsDocument && !bestContainsDocument) ||
                (containsDocument == bestContainsDocument && rootPath.Length > bestLength))
            {
                bestIndex = index;
                bestLength = rootPath.Length;
                bestContainsDocument = containsDocument;
            }
        }

        return bestIndex;
    }

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private static Document? FindDocument(Solution solution, string path)
    {
        return solution.Projects
            .SelectMany(static project => project.Documents)
            .FirstOrDefault(document => string.Equals(document.FilePath, path, PathComparison));
    }

    private static int GetOffset(SourceText text, Position position)
    {
        if (position.Line >= text.Lines.Count)
        {
            throw new ArgumentOutOfRangeException(
                nameof(position),
                position,
                "The position line is outside the document.");
        }

        TextLine line = text.Lines[position.Line];
        if (position.Character > line.Span.Length)
        {
            throw new ArgumentOutOfRangeException(
                nameof(position),
                position,
                "The position character is outside the line.");
        }

        return line.Start + position.Character;
    }

    private static void DisposeFolders(
        IEnumerable<(string RootPath, Workspace Workspace, Solution Solution)> folders)
    {
        foreach ((string _, Workspace workspace, Solution _) in folders)
        {
            workspace.Dispose();
        }
    }

    [LoggerMessage(
        EventId = 1,
        Level = LogLevel.Warning,
        Message = "MSBuild workspace diagnostic {Kind}: {Message}")]
    private partial void LogWorkspaceDiagnostic(WorkspaceDiagnosticKind kind, string message);

    [LoggerMessage(
        EventId = 2,
        Level = LogLevel.Information,
        Message = "Loaded {ProjectCount} projects from {WorkspaceFile}")]
    private partial void LogWorkspaceLoaded(int projectCount, string workspaceFile);
}
