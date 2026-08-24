using Csls.Protocol;
using Microsoft.Build.Locator;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.Formatting;
using Microsoft.CodeAnalysis.MSBuild;
using Microsoft.CodeAnalysis.Rename;
using Microsoft.CodeAnalysis.Text;
using Microsoft.Extensions.Logging;
using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using LspCodeAction = Csls.Protocol.CodeAction;
using LspDiagnostic = Csls.Protocol.Diagnostic;
using LspDiagnosticSeverity = Csls.Protocol.DiagnosticSeverity;
using LspDocumentHighlight = Csls.Protocol.DocumentHighlight;
using LspLocation = Csls.Protocol.Location;
using LspRange = Csls.Protocol.Range;
using LspSelectionRange = Csls.Protocol.SelectionRange;
using LspSignatureHelp = Csls.Protocol.SignatureHelp;
using LspSymbolKind = Csls.Protocol.SymbolKind;
using LspTextEdit = Csls.Protocol.TextEdit;
using RoslynDiagnostic = Microsoft.CodeAnalysis.Diagnostic;
using RoslynDiagnosticSeverity = Microsoft.CodeAnalysis.DiagnosticSeverity;
using RoslynLocation = Microsoft.CodeAnalysis.Location;
using RoslynSymbolKind = Microsoft.CodeAnalysis.SymbolKind;

namespace Csls.Workspaces;

/// <summary>
/// Loads multi-root Roslyn workspaces and publishes immutable document snapshots.
/// </summary>
public sealed partial class WorkspaceManager : IAsyncDisposable
{
    private const int MaximumDocumentSymbols = 2_000;
    private const int MaximumWorkspaceSymbols = 200;
    private const int MaximumSignatures = 100;
    private const int MaximumWorkspaceTextEdits = 10_000;
    private const string OrganizeImportsCodeActionKind = "source.organizeImports";
    private static readonly Lock s_msbuildRegistrationLock = new();
    private static bool s_msbuildRegistered;

    private readonly ILogger<WorkspaceManager> _logger;
    private readonly AnalyzerDiagnosticCache _diagnosticCache = new();
    private readonly SemaphoreSlim _mutationGate = new(1, 1);
    private readonly Dictionary<string, int> _documentVersions = new(
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    private ImmutableArray<(string RootPath, Workspace Workspace, Solution Solution)> _folders = [];
    private ImmutableDictionary<string, SourceText> _razorDocuments =
        ImmutableDictionary.Create<string, SourceText>(PathComparer);
    private int _enableAnalyzers = 1;
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

        ImmutableArray<(string RootPath, Workspace Workspace, Solution Solution)> loadedFolders =
            await LoadFoldersAsync(rootPaths, cancellationToken).ConfigureAwait(false);
        bool published = false;
        try
        {
            await PublishFoldersAsync(
                loadedFolders,
                documentVersions: null,
                razorDocuments: null,
                cancellationToken).ConfigureAwait(false);
            published = true;
        }
        finally
        {
            if (!published)
            {
                DisposeFolders(loadedFolders);
            }
        }
    }

    private async Task<ImmutableArray<(
        string RootPath,
        Workspace Workspace,
        Solution Solution)>> LoadFoldersAsync(
        IReadOnlyList<string> rootPaths,
        CancellationToken cancellationToken)
    {
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
                IReadOnlyList<string> workspaceFiles = WorkspaceDiscovery.Discover(
                    rootPath,
                    cancellationToken);
                if (workspaceFiles.Count == 0)
                {
                    (Workspace looseWorkspace, Solution looseSolution) = LoadLooseFiles(rootPath);
                    loadedFolders.Add((rootPath, looseWorkspace, looseSolution));
                    continue;
                }

                foreach (string workspaceFile in workspaceFiles)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    (Workspace Workspace, Solution Solution) loaded =
                        await LoadWorkspaceFileAsync(
                            workspaceFile,
                            cancellationToken).ConfigureAwait(false);
                    try
                    {
                        loadedFolders.Add((rootPath, loaded.Workspace, loaded.Solution));
                        LogWorkspaceLoaded(
                            loaded.Solution.ProjectIds.Count,
                            workspaceFile);
                    }
                    catch
                    {
                        loaded.Workspace.Dispose();
                        throw;
                    }
                }
            }

            return loadedFolders.MoveToImmutable();
        }
        catch
        {
            DisposeFolders(loadedFolders.ToImmutable());
            throw;
        }
    }

    private async Task<(Workspace Workspace, Solution Solution)> LoadWorkspaceFileAsync(
        string workspaceFile,
        CancellationToken cancellationToken)
    {
        EnsureMsBuildRegistered();
        var workspace = MSBuildWorkspace.Create();
        try
        {
            workspace.RegisterWorkspaceFailedHandler(eventArgs =>
                LogWorkspaceDiagnostic(
                    eventArgs.Diagnostic.Kind,
                    eventArgs.Diagnostic.Message));
            Solution solution;
            if (WorkspaceDiscovery.IsFileBasedApp(workspaceFile))
            {
                Project project = await FileBasedAppProjectLoader.OpenProjectAsync(
                    workspace,
                    workspaceFile,
                    LogWorkspaceDiagnostic,
                    cancellationToken).ConfigureAwait(false);
                solution = project.Solution;
            }
            else if (workspaceFile.EndsWith(
                ".csproj",
                StringComparison.OrdinalIgnoreCase))
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

            return (workspace, solution);
        }
        catch
        {
            workspace.Dispose();
            throw;
        }
    }

    private async Task PublishFoldersAsync(
        ImmutableArray<(string RootPath, Workspace Workspace, Solution Solution)> loadedFolders,
        IReadOnlyDictionary<string, int>? documentVersions,
        IReadOnlyDictionary<string, SourceText>? razorDocuments,
        CancellationToken cancellationToken)
    {
        await _mutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        ImmutableArray<(string RootPath, Workspace Workspace, Solution Solution)> previous;
        try
        {
            previous = _folders;
            _folders = loadedFolders;
            _documentVersions.Clear();
            _razorDocuments = razorDocuments is null
                ? ImmutableDictionary.Create<string, SourceText>(PathComparer)
                : razorDocuments.ToImmutableDictionary(PathComparer);
            if (documentVersions is not null)
            {
                foreach ((string path, int version) in documentVersions)
                {
                    _documentVersions.Add(path, version);
                }
            }

            _diagnosticCache.Clear();
            Interlocked.Increment(ref _generation);
        }
        finally
        {
            _mutationGate.Release();
        }

        DisposeFolders(previous);
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

            if (WorkspaceRazorDiagnosticService.IsRazorDocument(path))
            {
                var razorText = SourceText.From(document.Text, Encoding.UTF8);
                (string razorRootPath, Workspace razorWorkspace, Solution razorSolution) =
                    _folders[folderIndex];
                razorSolution = WithAdditionalDocumentText(razorSolution, path, razorText);
                _folders = _folders.SetItem(
                    folderIndex,
                    (razorRootPath, razorWorkspace, razorSolution));
                _razorDocuments = _razorDocuments.SetItem(
                    path,
                    razorText);
                _documentVersions[path] = document.Version;
                _diagnosticCache.Clear();
                Interlocked.Increment(ref _generation);
                return;
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

            _folders = _folders.SetItem(folderIndex, (rootPath, workspace, solution));
            _documentVersions[path] = document.Version;
            _diagnosticCache.Clear();
            Interlocked.Increment(ref _generation);
        }
        finally
        {
            _mutationGate.Release();
        }
    }

    /// <summary>
    /// Applies ordered incremental or full-text changes for an opened client document.
    /// </summary>
    /// <param name="parameters">The versioned document and ordered content changes.</param>
    /// <param name="cancellationToken">The operation cancellation token.</param>
    /// <returns>A task that completes after the mutation is published.</returns>
    public async Task ChangeDocumentAsync(
        DidChangeTextDocumentParams parameters,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        string path = parameters.TextDocument.Uri.GetFileSystemPath();
        await _mutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_documentVersions.TryGetValue(path, out int currentVersion))
            {
                throw new InvalidOperationException(
                    $"Document {path} must be opened before content changes are applied.");
            }

            if (parameters.TextDocument.Version <= currentVersion)
            {
                throw new InvalidOperationException(
                    $"Document version {parameters.TextDocument.Version} does not follow version {currentVersion}.");
            }

            int folderIndex = FindFolderIndex(path, _folders);
            if (folderIndex < 0)
            {
                throw new InvalidOperationException($"No workspace folder owns document {path}.");
            }

            if (_razorDocuments.TryGetValue(path, out SourceText? razorText))
            {
                razorText = ApplyContentChanges(
                    razorText,
                    parameters.ContentChanges,
                    cancellationToken);
                (string razorRootPath, Workspace razorWorkspace, Solution razorSolution) =
                    _folders[folderIndex];
                razorSolution = WithAdditionalDocumentText(razorSolution, path, razorText);
                _folders = _folders.SetItem(
                    folderIndex,
                    (razorRootPath, razorWorkspace, razorSolution));
                _razorDocuments = _razorDocuments.SetItem(
                    path,
                    razorText);
                _documentVersions[path] = parameters.TextDocument.Version;
                _diagnosticCache.Clear();
                Interlocked.Increment(ref _generation);
                return;
            }

            (string rootPath, Workspace workspace, Solution solution) = _folders[folderIndex];
            Document document = FindDocument(solution, path)
                ?? throw new InvalidOperationException($"Opened document {path} is unavailable.");
            SourceText text = await document.GetTextAsync(cancellationToken).ConfigureAwait(false);
            text = ApplyContentChanges(text, parameters.ContentChanges, cancellationToken);

            Solution changedSolution = solution.WithDocumentText(
                document.Id,
                text,
                PreservationMode.PreserveIdentity);
            _folders = _folders.SetItem(folderIndex, (rootPath, workspace, changedSolution));
            _documentVersions[path] = parameters.TextDocument.Version;
            _diagnosticCache.Clear();
            Interlocked.Increment(ref _generation);
        }
        finally
        {
            _mutationGate.Release();
        }
    }

    /// <summary>
    /// Removes an open overlay and restores the document contents persisted on disk.
    /// </summary>
    /// <param name="parameters">The closed text document.</param>
    /// <param name="cancellationToken">The operation cancellation token.</param>
    /// <returns>A task that completes after the persisted snapshot is published.</returns>
    public async Task CloseDocumentAsync(
        DidCloseTextDocumentParams parameters,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        string path = parameters.TextDocument.Uri.GetFileSystemPath();
        await _mutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_documentVersions.Remove(path))
            {
                return;
            }

            int folderIndex = FindFolderIndex(path, _folders);
            if (folderIndex < 0)
            {
                return;
            }

            if (WorkspaceRazorDiagnosticService.IsRazorDocument(path))
            {
                _razorDocuments = _razorDocuments.Remove(path);
                (string razorRootPath, Workspace razorWorkspace, Solution razorSolution) =
                    _folders[folderIndex];
                razorSolution = await RestoreAdditionalDocumentTextAsync(
                    razorSolution,
                    path,
                    cancellationToken).ConfigureAwait(false);
                _folders = _folders.SetItem(
                    folderIndex,
                    (razorRootPath, razorWorkspace, razorSolution));
                _diagnosticCache.Clear();
                Interlocked.Increment(ref _generation);
                return;
            }

            (string rootPath, Workspace workspace, Solution solution) = _folders[folderIndex];
            Document? document = FindDocument(solution, path);
            if (document is null)
            {
                return;
            }

            Solution changedSolution;
            if (File.Exists(path))
            {
                string persistedText = await File.ReadAllTextAsync(path, cancellationToken)
                    .ConfigureAwait(false);
                changedSolution = solution.WithDocumentText(
                    document.Id,
                    SourceText.From(persistedText, Encoding.UTF8),
                    PreservationMode.PreserveIdentity);
            }
            else
            {
                changedSolution = solution.RemoveDocument(document.Id);
            }

            _folders = _folders.SetItem(folderIndex, (rootPath, workspace, changedSolution));
            _diagnosticCache.Clear();
            Interlocked.Increment(ref _generation);
        }
        finally
        {
            _mutationGate.Release();
        }
    }

    /// <summary>
    /// Gets compiler and analyzer diagnostics for one immutable document snapshot.
    /// </summary>
    /// <param name="parameters">The document and optional prior result identifier.</param>
    /// <param name="cancellationToken">The operation cancellation token.</param>
    /// <returns>A complete or unchanged LSP diagnostic report.</returns>
    public async Task<DocumentDiagnosticReport> GetDiagnosticsAsync(
        DocumentDiagnosticParams parameters,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        long generation = Generation;
        string resultId = generation.ToString(CultureInfo.InvariantCulture);
        if (string.Equals(parameters.PreviousResultId, resultId, StringComparison.Ordinal))
        {
            return new DocumentDiagnosticReport
            {
                Kind = "unchanged",
                ResultId = resultId
            };
        }

        string path = parameters.TextDocument.Uri.GetFileSystemPath();
        ImmutableArray<(string RootPath, Workspace Workspace, Solution Solution)> folders = _folders;
        int folderIndex = FindFolderIndex(path, folders);
        if (folderIndex >= 0 && WorkspaceRazorDiagnosticService.IsRazorDocument(path))
        {
            SourceText? razorText = _razorDocuments.GetValueOrDefault(path);
            if (razorText is null && File.Exists(path))
            {
                string persistedText = await File.ReadAllTextAsync(path, cancellationToken)
                    .ConfigureAwait(false);
                razorText = SourceText.From(persistedText, Encoding.UTF8);
            }

            ImmutableArray<RoslynDiagnostic>.Builder razorProjectDiagnostics =
                ImmutableArray.CreateBuilder<RoslynDiagnostic>();
            if (razorText is not null)
            {
                foreach (Project project in folders[folderIndex].Solution.Projects)
                {
                    if (!ContainsAdditionalDocument(project, path))
                    {
                        continue;
                    }

                    ImmutableArray<RoslynDiagnostic> projectResult =
                        await _diagnosticCache.GetOrAddAsync(
                            generation,
                            project,
                            ComputeProjectDiagnosticsAsync,
                            cancellationToken).ConfigureAwait(false);
                    razorProjectDiagnostics.AddRange(projectResult);
                }
            }

            return new DocumentDiagnosticReport
            {
                Kind = "full",
                ResultId = resultId,
                Items = razorText is null
                    ? []
                    : WorkspaceRazorDiagnosticService.GetDiagnostics(
                        path,
                        razorText,
                        razorProjectDiagnostics,
                        cancellationToken)
            };
        }

        Document? document = folderIndex >= 0
            ? FindDocument(folders[folderIndex].Solution, path)
            : null;
        if (document is null)
        {
            return new DocumentDiagnosticReport
            {
                Kind = "full",
                ResultId = resultId,
                Items = []
            };
        }

        ImmutableArray<RoslynDiagnostic> projectDiagnostics =
            await _diagnosticCache.GetOrAddAsync(
                generation,
                document.Project,
                ComputeProjectDiagnosticsAsync,
                cancellationToken).ConfigureAwait(false);
        LspDiagnostic[] diagnostics =
        [
            .. projectDiagnostics
                .Where(diagnostic => IsDocumentDiagnostic(diagnostic, path))
                .OrderBy(static diagnostic => diagnostic.Location.SourceSpan.Start)
                .ThenBy(static diagnostic => diagnostic.Id, StringComparer.Ordinal)
                .Select(ToLspDiagnostic)
        ];
        return new DocumentDiagnosticReport
        {
            Kind = "full",
            ResultId = resultId,
            Items = diagnostics
        };
    }

    /// <summary>
    /// Gets the client version for a document with a live open overlay.
    /// </summary>
    /// <param name="uri">The document URI to inspect.</param>
    /// <returns>The open version, or null when the document is closed.</returns>
    public int? GetOpenDocumentVersion(DocumentUri uri)
    {
        string path = uri.GetFileSystemPath();
        return _documentVersions.TryGetValue(path, out int version) ? version : null;
    }

    /// <summary>
    /// Finds source definitions for the symbol at one immutable document position.
    /// </summary>
    /// <param name="parameters">The target document position.</param>
    /// <param name="cancellationToken">The operation cancellation token.</param>
    /// <returns>The bounded source definition locations.</returns>
    public async Task<IReadOnlyList<LspLocation>> GetDefinitionsAsync(
        TextDocumentPositionParams parameters,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        return await GetNavigationAsync(
            parameters,
            WorkspaceNavigationService.GetDefinitionsAsync,
            WorkspaceRazorNavigationService.GetDefinitionsAsync,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Finds source declarations for the symbol at one immutable document position.
    /// </summary>
    /// <param name="parameters">The target document position.</param>
    /// <param name="cancellationToken">The operation cancellation token.</param>
    /// <returns>The bounded source declaration locations.</returns>
    public async Task<IReadOnlyList<LspLocation>> GetDeclarationsAsync(
        TextDocumentPositionParams parameters,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        return await GetNavigationAsync(
            parameters,
            WorkspaceNavigationService.GetDeclarationsAsync,
            WorkspaceRazorNavigationService.GetDeclarationsAsync,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Finds source definitions for the type of a symbol at one immutable document position.
    /// </summary>
    /// <param name="parameters">The target document position.</param>
    /// <param name="cancellationToken">The operation cancellation token.</param>
    /// <returns>The bounded source type-definition locations.</returns>
    public async Task<IReadOnlyList<LspLocation>> GetTypeDefinitionsAsync(
        TextDocumentPositionParams parameters,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        return await GetNavigationAsync(
            parameters,
            WorkspaceNavigationService.GetTypeDefinitionsAsync,
            WorkspaceRazorNavigationService.GetTypeDefinitionsAsync,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Finds source implementations for a symbol at one immutable document position.
    /// </summary>
    /// <param name="parameters">The target document position.</param>
    /// <param name="cancellationToken">The operation cancellation token.</param>
    /// <returns>The bounded source implementation locations.</returns>
    public async Task<IReadOnlyList<LspLocation>> GetImplementationsAsync(
        TextDocumentPositionParams parameters,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        return await GetNavigationAsync(
            parameters,
            WorkspaceNavigationService.GetImplementationsAsync,
            WorkspaceRazorNavigationService.GetImplementationsAsync,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<LspLocation>> GetNavigationAsync(
        TextDocumentPositionParams parameters,
        Func<Document?, Position, CancellationToken, Task<IReadOnlyList<LspLocation>>>
            getDocumentNavigationAsync,
        Func<Solution, string, Position, CancellationToken, Task<IReadOnlyList<LspLocation>>>
            getRazorNavigationAsync,
        CancellationToken cancellationToken)
    {
        string path = parameters.TextDocument.Uri.GetFileSystemPath();
        ImmutableArray<(string RootPath, Workspace Workspace, Solution Solution)> folders = _folders;
        int folderIndex = FindFolderIndex(path, folders);
        if (folderIndex < 0)
        {
            return [];
        }

        Solution solution = folders[folderIndex].Solution;
        return WorkspaceRazorDiagnosticService.IsRazorDocument(path)
            ? await getRazorNavigationAsync(
                solution,
                path,
                parameters.Position,
                cancellationToken).ConfigureAwait(false)
            : await getDocumentNavigationAsync(
                FindDocument(solution, path),
                parameters.Position,
                cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Gets nested syntax selections for ordered positions in one immutable document snapshot.
    /// </summary>
    /// <param name="parameters">The target document and ordered UTF-16 positions.</param>
    /// <param name="cancellationToken">The operation cancellation token.</param>
    /// <returns>One inner-to-outer selection hierarchy per position.</returns>
    public async Task<IReadOnlyList<LspSelectionRange>> GetSelectionRangesAsync(
        SelectionRangeParams parameters,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        return await WorkspaceNavigationService.GetSelectionRangesAsync(
            FindCurrentDocument(parameters.TextDocument.Uri),
            parameters.Positions,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Gets bounded foldable ranges from one immutable document snapshot.
    /// </summary>
    /// <param name="parameters">The target text document.</param>
    /// <param name="maximumRangeCount">The negotiated maximum result count.</param>
    /// <param name="lineFoldingOnly">Whether the client accepts line-only ranges.</param>
    /// <param name="includeCollapsedText">Whether collapsed display text is supported.</param>
    /// <param name="includeCommentKind">Whether the comment kind is supported.</param>
    /// <param name="includeImportsKind">Whether the imports kind is supported.</param>
    /// <param name="includeRegionKind">Whether the region kind is supported.</param>
    /// <param name="cancellationToken">The operation cancellation token.</param>
    /// <returns>The ordered syntax, comment, import, and region folding ranges.</returns>
    public async Task<IReadOnlyList<FoldingRange>> GetFoldingRangesAsync(
        FoldingRangeParams parameters,
        int maximumRangeCount,
        bool lineFoldingOnly,
        bool includeCollapsedText,
        bool includeCommentKind,
        bool includeImportsKind,
        bool includeRegionKind,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        return await WorkspaceFoldingRangeService.GetFoldingRangesAsync(
            FindCurrentDocument(parameters.TextDocument.Uri),
            maximumRangeCount,
            lineFoldingOnly,
            includeCollapsedText,
            includeCommentKind,
            includeImportsKind,
            includeRegionKind,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Gets paired XML documentation names at one immutable document position.
    /// </summary>
    /// <param name="parameters">The target document position.</param>
    /// <param name="cancellationToken">The operation cancellation token.</param>
    /// <returns>The linked ranges, or <see langword="null"/> when none apply.</returns>
    public async Task<LinkedEditingRanges?> GetLinkedEditingRangesAsync(
        LinkedEditingRangeParams parameters,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        return await WorkspaceLinkedEditingService.GetLinkedEditingRangesAsync(
            FindCurrentDocument(parameters.TextDocument.Uri),
            parameters.Position,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Gets semantic symbol occurrences within one immutable source document snapshot.
    /// </summary>
    /// <param name="parameters">The target document position.</param>
    /// <param name="cancellationToken">The operation cancellation token.</param>
    /// <returns>The bounded ordered read, write, and declaration highlights.</returns>
    public async Task<IReadOnlyList<LspDocumentHighlight>> GetDocumentHighlightsAsync(
        TextDocumentPositionParams parameters,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        return await WorkspaceNavigationService.GetDocumentHighlightsAsync(
            FindCurrentDocument(parameters.TextDocument.Uri),
            parameters.Position,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Gets navigable links to existing resources named by active C# directives.
    /// </summary>
    /// <param name="parameters">The target text document.</param>
    /// <param name="cancellationToken">The operation cancellation token.</param>
    /// <returns>The bounded ordered document links.</returns>
    public async Task<IReadOnlyList<DocumentLink>> GetDocumentLinksAsync(
        DocumentLinkParams parameters,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        return await WorkspaceDocumentLinkService.GetDocumentLinksAsync(
            FindCurrentDocument(parameters.TextDocument.Uri),
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Gets the stable .NET moniker for one symbol in an immutable document snapshot.
    /// </summary>
    /// <param name="parameters">The target document position.</param>
    /// <param name="cancellationToken">The operation cancellation token.</param>
    /// <returns>The resolved moniker, or an empty list when no stable identity exists.</returns>
    public async Task<IReadOnlyList<Moniker>> GetMonikersAsync(
        MonikerParams parameters,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        return await WorkspaceMonikerService.GetMonikersAsync(
            FindCurrentDocument(parameters.TextDocument.Uri),
            parameters.Position,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Gets source text for one generated or metadata-backed virtual C# document.
    /// </summary>
    /// <param name="parameters">The target virtual document.</param>
    /// <param name="cancellationToken">The operation cancellation token.</param>
    /// <returns>The resolved virtual document, when present.</returns>
    public async Task<CSharpMetadataResponse?> GetCSharpMetadataAsync(
        CSharpMetadataParams parameters,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        ImmutableArray<(string RootPath, Workspace Workspace, Solution Solution)> folders = _folders;
        return await WorkspaceVirtualDocumentService.GetAsync(
            folders.SelectMany(static folder => folder.Solution.Projects),
            parameters.TextDocument.Uri,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Gets complete relative-encoded semantic tokens for one immutable document snapshot.
    /// </summary>
    /// <param name="parameters">The target text document.</param>
    /// <param name="cancellationToken">The operation cancellation token.</param>
    /// <returns>The complete five-integer token encoding.</returns>
    public async Task<IReadOnlyList<int>> GetSemanticTokensAsync(
        SemanticTokensParams parameters,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        return await WorkspaceSemanticTokensService.GetSemanticTokensAsync(
            FindCurrentDocument(parameters.TextDocument.Uri),
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Finds source references for the symbol at one immutable document position.
    /// </summary>
    /// <param name="parameters">The target position and declaration inclusion behavior.</param>
    /// <param name="cancellationToken">The operation cancellation token.</param>
    /// <returns>The bounded deduplicated source reference locations.</returns>
    public async Task<IReadOnlyList<LspLocation>> GetReferencesAsync(
        ReferenceParams parameters,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        string path = parameters.TextDocument.Uri.GetFileSystemPath();
        ImmutableArray<(string RootPath, Workspace Workspace, Solution Solution)> folders = _folders;
        int folderIndex = FindFolderIndex(path, folders);
        if (folderIndex < 0)
        {
            return [];
        }

        Solution solution = folders[folderIndex].Solution;
        if (WorkspaceRazorDiagnosticService.IsRazorDocument(path))
        {
            return await WorkspaceRazorNavigationService.GetReferencesAsync(
                solution,
                path,
                parameters.Position,
                parameters.Context.IncludeDeclaration,
                cancellationToken).ConfigureAwait(false);
        }

        return await WorkspaceNavigationService.GetReferencesAsync(
            FindDocument(solution, path),
            parameters.Position,
            parameters.Context.IncludeDeclaration,
            cancellationToken).ConfigureAwait(false);
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

        Solution solution = folders[folderIndex].Solution;
        if (WorkspaceRazorDiagnosticService.IsRazorDocument(path))
        {
            return await WorkspaceRazorHoverService.GetHoverAsync(
                solution,
                path,
                parameters.Position,
                cancellationToken).ConfigureAwait(false);
        }

        Document? document = FindDocument(solution, path);
        if (document is null)
        {
            return null;
        }

        SourceText text = await document.GetTextAsync(cancellationToken).ConfigureAwait(false);
        int offset = LspPositionConverter.GetOffset(text, parameters.Position);
        (string Markdown, TextSpan Span)? hover = await WorkspaceHoverService
            .GetAsync(document, offset, cancellationToken)
            .ConfigureAwait(false);
        if (hover is null)
        {
            return null;
        }

        LinePositionSpan lineSpan = text.Lines.GetLinePositionSpan(hover.Value.Span);
        return new Hover
        {
            Contents = new MarkupContent
            {
                Kind = "markdown",
                Value = hover.Value.Markdown
            },
            Range = new LspRange(
                new Position(lineSpan.Start.Line, lineSpan.Start.Character),
                new Position(lineSpan.End.Line, lineSpan.End.Character))
        };
    }

    /// <summary>
    /// Gets the bounded hierarchical declaration tree for one immutable document snapshot.
    /// </summary>
    /// <param name="parameters">The target document.</param>
    /// <param name="cancellationToken">The operation cancellation token.</param>
    /// <returns>The declaration hierarchy in source order.</returns>
    public async Task<IReadOnlyList<DocumentSymbol>> GetDocumentSymbolsAsync(
        DocumentSymbolParams parameters,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        string path = parameters.TextDocument.Uri.GetFileSystemPath();
        ImmutableArray<(string RootPath, Workspace Workspace, Solution Solution)> folders = _folders;
        int folderIndex = FindFolderIndex(path, folders);
        Document? document = folderIndex >= 0
            ? FindDocument(folders[folderIndex].Solution, path)
            : null;
        if (document is null)
        {
            return [];
        }

        SyntaxNode root = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Roslyn returned no syntax root.");
        SemanticModel semanticModel = await document
            .GetSemanticModelAsync(cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException("Roslyn returned no semantic model.");
        SourceText text = await document.GetTextAsync(cancellationToken).ConfigureAwait(false);
        var symbols = new List<DocumentSymbol>();
        int symbolCount = 0;
        AddDocumentSymbols(
            root.ChildNodes(),
            symbols,
            semanticModel,
            text,
            ref symbolCount,
            cancellationToken);
        return symbols;
    }

    /// <summary>
    /// Searches source declarations across every immutable workspace snapshot.
    /// </summary>
    /// <param name="parameters">The client declaration pattern.</param>
    /// <param name="cancellationToken">The operation cancellation token.</param>
    /// <returns>Bounded ordered workspace symbols with lazily resolved ranges.</returns>
    public async Task<IReadOnlyList<WorkspaceSymbol>> GetWorkspaceSymbolsAsync(
        WorkspaceSymbolParams parameters,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        string query = parameters.Query.Trim();
        long generation = Generation;
        ImmutableArray<(string RootPath, Workspace Workspace, Solution Solution)> folders = _folders;
        var symbols = new Dictionary<string, WorkspaceSymbol>(StringComparer.Ordinal);
        foreach ((string _, Workspace _, Solution solution) in folders)
        {
            cancellationToken.ThrowIfCancellationRequested();
            IEnumerable<ISymbol> declarations = query.Length == 0
                ? await SymbolFinder.FindSourceDeclarationsAsync(
                    solution,
                    static _ => true,
                    SymbolFilter.TypeAndMember,
                    cancellationToken).ConfigureAwait(false)
                : await SymbolFinder.FindSourceDeclarationsWithPatternAsync(
                    solution,
                    query,
                    SymbolFilter.TypeAndMember,
                    cancellationToken).ConfigureAwait(false);
            foreach (ISymbol declaration in declarations)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (declaration.IsImplicitlyDeclared ||
                    !declaration.CanBeReferencedByName ||
                    string.IsNullOrWhiteSpace(declaration.Name))
                {
                    continue;
                }

                LspLocation? location = declaration.Locations
                    .Select(ToLspLocation)
                    .FirstOrDefault(static candidate => candidate is not null);
                if (location is null)
                {
                    continue;
                }

                LspSymbolKind kind = GetSymbolKind(declaration);
                string key = string.Create(
                    CultureInfo.InvariantCulture,
                    $"{location.Uri}|{location.Range.Start.Line}|{location.Range.Start.Character}|{declaration.Name}|{kind}");
                symbols.TryAdd(
                    key,
                    new WorkspaceSymbol
                    {
                        Name = declaration.Name,
                        Kind = kind,
                        ContainerName = GetContainerName(declaration),
                        Location = new WorkspaceSymbolLocation
                        {
                            Uri = location.Uri
                        },
                        Data = new WorkspaceSymbolData
                        {
                            Generation = generation,
                            Range = location.Range
                        }
                    });
            }
        }

        return
        [
            .. symbols.Values
                .OrderBy(symbol => GetWorkspaceSymbolMatchRank(symbol.Name, query))
                .ThenBy(static symbol => symbol.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(static symbol => symbol.ContainerName, StringComparer.Ordinal)
                .ThenBy(static symbol => symbol.Location.Uri.ToString(), StringComparer.Ordinal)
                .Take(MaximumWorkspaceSymbols)
        ];
    }

    /// <summary>
    /// Completes the source range of one workspace symbol from its immutable resolve data.
    /// </summary>
    /// <param name="symbol">The workspace symbol returned by this server.</param>
    /// <returns>The same symbol with its exact source range populated.</returns>
    public WorkspaceSymbol ResolveWorkspaceSymbol(WorkspaceSymbol symbol)
    {
        ArgumentNullException.ThrowIfNull(symbol);
        WorkspaceSymbolData data = symbol.Data
            ?? throw new InvalidDataException("The workspace symbol contains no resolve data.");
        if (data.Generation != Generation)
        {
            throw new InvalidOperationException(
                "The workspace symbol belongs to a retired workspace generation.");
        }

        return symbol with
        {
            Location = symbol.Location with { Range = data.Range }
        };
    }

    /// <summary>
    /// Gets bounded overload-aware signature help for one immutable document position.
    /// </summary>
    /// <param name="parameters">The document position and trigger context.</param>
    /// <param name="cancellationToken">The operation cancellation token.</param>
    /// <returns>Signature help, or null when the position is not inside a supported argument list.</returns>
    public async Task<LspSignatureHelp?> GetSignatureHelpAsync(
        SignatureHelpParams parameters,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        string path = parameters.TextDocument.Uri.GetFileSystemPath();
        ImmutableArray<(string RootPath, Workspace Workspace, Solution Solution)> folders = _folders;
        int folderIndex = FindFolderIndex(path, folders);
        Document? document = folderIndex >= 0
            ? FindDocument(folders[folderIndex].Solution, path)
            : null;
        if (document is null)
        {
            return null;
        }

        SourceText text = await document.GetTextAsync(cancellationToken).ConfigureAwait(false);
        int offset = LspPositionConverter.GetOffset(text, parameters.Position);
        SyntaxNode root = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Roslyn returned no syntax root.");
        BaseArgumentListSyntax? argumentList = FindArgumentList(root, offset);
        if (argumentList is null)
        {
            return null;
        }

        SemanticModel semanticModel = await document
            .GetSemanticModelAsync(cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException("Roslyn returned no semantic model.");
        (IReadOnlyList<IMethodSymbol> methods, IMethodSymbol? selectedMethod) =
            GetSignatureMethods(argumentList, semanticModel, cancellationToken);
        if (methods.Count == 0)
        {
            return null;
        }

        int activeParameter = argumentList.Arguments
            .GetSeparators()
            .Count(separator => separator.SpanStart < offset);
        int activeSignature = selectedMethod is null
            ? FindBestSignature(methods, activeParameter)
            : FindSignatureIndex(methods, selectedMethod);
        SignatureInformation[] signatures =
        [
            .. methods
                .Take(MaximumSignatures)
                .Select(method => CreateSignatureInformation(method, activeParameter))
        ];
        activeSignature = Math.Clamp(activeSignature, 0, signatures.Length - 1);
        return new LspSignatureHelp
        {
            Signatures = signatures,
            ActiveSignature = activeSignature,
            ActiveParameter = activeParameter
        };
    }

    /// <summary>
    /// Validates a source symbol and returns the identifier range accepted by rename.
    /// </summary>
    /// <param name="parameters">The target document position.</param>
    /// <param name="cancellationToken">The operation cancellation token.</param>
    /// <returns>The rename range and placeholder, or null when rename is unavailable.</returns>
    public async Task<PrepareRenameResult?> PrepareRenameAsync(
        TextDocumentPositionParams parameters,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        string path = parameters.TextDocument.Uri.GetFileSystemPath();
        ImmutableArray<(string RootPath, Workspace Workspace, Solution Solution)> folders =
            _folders;
        int folderIndex = FindFolderIndex(path, folders);
        if (folderIndex >= 0 && WorkspaceRazorDiagnosticService.IsRazorDocument(path))
        {
            (RazorMappedDocument? _, ISymbol? razorSymbol, LspRange? razorRange) =
                await WorkspaceRazorRenameService.ResolveTargetAsync(
                    folders[folderIndex].Solution,
                    path,
                    parameters.Position,
                    cancellationToken).ConfigureAwait(false);
            razorSymbol = NormalizeRenameSymbol(razorSymbol);
            return razorSymbol is not null &&
                razorRange is not null &&
                CanRenameSymbol(razorSymbol)
                    ? new PrepareRenameResult
                    {
                        Range = razorRange.Value,
                        Placeholder = razorSymbol.Name
                    }
                    : null;
        }

        (Document? document, ISymbol? symbol) = await FindSymbolAsync(
            parameters,
            cancellationToken).ConfigureAwait(false);
        symbol = NormalizeRenameSymbol(symbol);
        if (document is null || symbol is null || !CanRenameSymbol(symbol))
        {
            return null;
        }

        SourceText text = await document.GetTextAsync(cancellationToken).ConfigureAwait(false);
        int offset = LspPositionConverter.GetOffset(text, parameters.Position);
        SyntaxNode root = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Roslyn returned no syntax root.");
        int tokenOffset = Math.Clamp(
            offset == root.FullSpan.End ? offset - 1 : offset,
            0,
            Math.Max(0, root.FullSpan.End - 1));
        SyntaxToken token = root.FindToken(tokenOffset, findInsideTrivia: true);
        return new PrepareRenameResult
        {
            Range = ToLspRange(text, token.Span),
            Placeholder = symbol.Name
        };
    }

    /// <summary>
    /// Computes a bounded version-aware workspace edit for one Roslyn symbol rename.
    /// </summary>
    /// <param name="parameters">The target symbol and requested replacement identifier.</param>
    /// <param name="cancellationToken">The operation cancellation token.</param>
    /// <returns>The complete cross-document rename edit.</returns>
    public async Task<WorkspaceEdit> GetRenameEditAsync(
        RenameParams parameters,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        ArgumentException.ThrowIfNullOrWhiteSpace(parameters.NewName);
        if (!SyntaxFacts.IsValidIdentifier(parameters.NewName))
        {
            throw new InvalidDataException(
                $"'{parameters.NewName}' is not a valid C# identifier.");
        }

        var positionParameters = new TextDocumentPositionParams
        {
            TextDocument = parameters.TextDocument,
            Position = parameters.Position
        };
        string path = parameters.TextDocument.Uri.GetFileSystemPath();
        ImmutableArray<(string RootPath, Workspace Workspace, Solution Solution)> folders =
            _folders;
        int folderIndex = FindFolderIndex(path, folders);
        (Document? document, ISymbol? symbol) =
            folderIndex >= 0 && WorkspaceRazorDiagnosticService.IsRazorDocument(path)
                ? await ResolveRazorRenameSymbolAsync(
                    folders[folderIndex].Solution,
                    path,
                    parameters.Position,
                    cancellationToken).ConfigureAwait(false)
                : await FindSymbolAsync(
                    positionParameters,
                    cancellationToken).ConfigureAwait(false);
        symbol = NormalizeRenameSymbol(symbol);
        if (document is null || symbol is null || !CanRenameSymbol(symbol))
        {
            throw new InvalidOperationException(
                "The requested document position does not contain a renameable source symbol.");
        }

        Solution originalSolution = document.Project.Solution;
        Solution renamedSolution = await Renamer.RenameSymbolAsync(
            originalSolution,
            symbol,
            new SymbolRenameOptions(
                RenameOverloads: false,
                RenameInStrings: false,
                RenameInComments: false,
                RenameFile: false),
            parameters.NewName,
            cancellationToken).ConfigureAwait(false);
        IReadOnlyDictionary<string, IReadOnlyList<LspTextEdit>> razorEdits =
            await WorkspaceRazorRenameService.GetMappedEditsAsync(
                originalSolution,
                symbol,
                parameters.NewName,
                cancellationToken).ConfigureAwait(false);
        return await CreateWorkspaceEditAsync(
            originalSolution,
            renamedSolution,
            razorEdits,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Gets concrete Roslyn code actions supported for one immutable document snapshot.
    /// </summary>
    /// <param name="parameters">The target range and requested action context.</param>
    /// <param name="cancellationToken">The operation cancellation token.</param>
    /// <returns>The supported actions with concrete version-aware edits.</returns>
    public async Task<IReadOnlyList<LspCodeAction>> GetCodeActionsAsync(
        CodeActionParams parameters,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        if (!IsCodeActionRequested(parameters.Context.Only, OrganizeImportsCodeActionKind))
        {
            return [];
        }

        Document? document = FindDocument(parameters.TextDocument.Uri);
        if (document is null)
        {
            return [];
        }

        Document organizedDocument = await Formatter.OrganizeImportsAsync(
            document,
            cancellationToken).ConfigureAwait(false);
        WorkspaceEdit edit = await CreateWorkspaceEditAsync(
            document.Project.Solution,
            organizedDocument.Project.Solution,
            cancellationToken).ConfigureAwait(false);
        if (edit.DocumentChanges.Count == 0)
        {
            return [];
        }

        return
        [
            new LspCodeAction
            {
                Title = "Organize Imports",
                Kind = OrganizeImportsCodeActionKind,
                IsPreferred = true,
                Edit = edit
            }
        ];
    }

    /// <summary>
    /// Binds a workspace edit to the current generation and exact document content hashes.
    /// </summary>
    /// <param name="edit">The concrete workspace edit produced from the current snapshot.</param>
    /// <param name="cancellationToken">The operation cancellation token.</param>
    /// <returns>The immutable edit snapshot and application preconditions.</returns>
    public async Task<WorkspaceEditSnapshot> CreateEditSnapshotAsync(
        WorkspaceEdit edit,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(edit);
        long generation = Generation;
        var preconditions = new List<DocumentEditPrecondition>(edit.DocumentChanges.Count);
        foreach (TextDocumentEdit documentEdit in edit.DocumentChanges)
        {
            cancellationToken.ThrowIfCancellationRequested();
            TextDocument document = FindTextDocument(documentEdit.TextDocument.Uri)
                ?? throw new InvalidOperationException(
                    $"Edited document {documentEdit.TextDocument.Uri} is unavailable.");
            SourceText text = await document.GetTextAsync(cancellationToken).ConfigureAwait(false);
            preconditions.Add(new DocumentEditPrecondition
            {
                Uri = documentEdit.TextDocument.Uri,
                Version = documentEdit.TextDocument.Version,
                Sha256 = ComputeTextHash(text.ToString())
            });
        }

        if (Generation != generation)
        {
            throw new InvalidOperationException(
                "The workspace changed while edit preconditions were being computed.");
        }

        return new WorkspaceEditSnapshot
        {
            WorkspaceGeneration = generation,
            Edit = edit,
            Preconditions = preconditions
        };
    }

    /// <summary>
    /// Wraps one document's text edits in a version-aware workspace edit.
    /// </summary>
    /// <param name="uri">The target source document URI.</param>
    /// <param name="edits">The bounded non-overlapping text edits.</param>
    /// <returns>The version-aware workspace edit.</returns>
    public WorkspaceEdit CreateDocumentWorkspaceEdit(
        DocumentUri uri,
        IReadOnlyList<LspTextEdit> edits)
    {
        ArgumentNullException.ThrowIfNull(edits);
        if (edits.Count == 0)
        {
            return new WorkspaceEdit { DocumentChanges = [] };
        }

        string path = uri.GetFileSystemPath();
        return new WorkspaceEdit
        {
            DocumentChanges =
            [
                new TextDocumentEdit
                {
                    TextDocument = new OptionalVersionedTextDocumentIdentifier
                    {
                        Uri = uri,
                        Version = _documentVersions.TryGetValue(path, out int version)
                            ? version
                            : null
                    },
                    Edits = edits
                }
            ]
        };
    }

    /// <summary>
    /// Applies a closed-document edit snapshot after generation and SHA-256 validation.
    /// </summary>
    /// <param name="snapshot">The one-use edit snapshot to apply.</param>
    /// <param name="cancellationToken">The operation cancellation token.</param>
    /// <returns>The workspace generation published after application.</returns>
    public async Task<long> ApplyWorkspaceEditAsync(
        WorkspaceEditSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        await _mutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (Generation != snapshot.WorkspaceGeneration)
            {
                throw new InvalidOperationException(
                    "The edit plan belongs to a retired workspace generation.");
            }

            var preconditions = snapshot
                .Preconditions
                .ToDictionary(static precondition => precondition.Uri);
            if (preconditions.Count != snapshot.Edit.DocumentChanges.Count)
            {
                throw new InvalidDataException(
                    "The edit plan does not contain exactly one precondition per document.");
            }

            if (snapshot.Edit.DocumentChanges.Count == 0)
            {
                return Generation;
            }

            var stagedFiles = new Dictionary<
                string,
                (string TempPath, string BackupPath, SourceText Text)>(PathComparer);
            try
            {
                foreach (TextDocumentEdit documentEdit in snapshot.Edit.DocumentChanges)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!preconditions.TryGetValue(
                        documentEdit.TextDocument.Uri,
                        out DocumentEditPrecondition? precondition))
                    {
                        throw new InvalidDataException(
                            $"The edit plan is missing a precondition for {documentEdit.TextDocument.Uri}.");
                    }

                    if (precondition.Version is not null ||
                        documentEdit.TextDocument.Version is not null)
                    {
                        throw new InvalidOperationException(
                            $"Document {documentEdit.TextDocument.Uri} is owned by an editor; " +
                            "apply the returned workspace edit through that editor.");
                    }

                    string path = documentEdit.TextDocument.Uri.GetFileSystemPath();
                    if (!File.Exists(path))
                    {
                        throw new FileNotFoundException(
                            "An edited source document no longer exists.",
                            path);
                    }

                    string persistedText = await File.ReadAllTextAsync(path, cancellationToken)
                        .ConfigureAwait(false);
                    if (!string.Equals(
                        ComputeTextHash(persistedText),
                        precondition.Sha256,
                        StringComparison.Ordinal))
                    {
                        throw new InvalidOperationException(
                            $"Document {path} changed after the edit plan was created.");
                    }

                    TextDocument document = FindTextDocument(documentEdit.TextDocument.Uri)
                        ?? throw new InvalidOperationException(
                            $"Edited document {documentEdit.TextDocument.Uri} is unavailable.");
                    SourceText workspaceText = await document
                        .GetTextAsync(cancellationToken)
                        .ConfigureAwait(false);
                    var originalText = SourceText.From(
                        persistedText,
                        workspaceText.Encoding ?? new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                        workspaceText.ChecksumAlgorithm);
                    SourceText changedText = ApplyTextEdits(originalText, documentEdit.Edits);
                    string tempPath = path + $".csls-{Guid.NewGuid():N}.tmp";
                    string backupPath = path + $".csls-{Guid.NewGuid():N}.bak";
                    await File.WriteAllTextAsync(
                        tempPath,
                        changedText.ToString(),
                        changedText.Encoding ?? new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                        cancellationToken).ConfigureAwait(false);
                    stagedFiles.Add(path, (tempPath, backupPath, changedText));
                }

                cancellationToken.ThrowIfCancellationRequested();
                ReplaceStagedFiles(stagedFiles);
                PublishAppliedTexts(stagedFiles);
                _diagnosticCache.Clear();
                return Interlocked.Increment(ref _generation);
            }
            finally
            {
                CleanupStagedFiles(stagedFiles);
            }
        }
        finally
        {
            _mutationGate.Release();
        }
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

    private static void AddDocumentSymbols(
        IEnumerable<SyntaxNode> nodes,
        List<DocumentSymbol> target,
        SemanticModel semanticModel,
        SourceText text,
        ref int symbolCount,
        CancellationToken cancellationToken)
    {
        foreach (SyntaxNode node in nodes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (symbolCount >= MaximumDocumentSymbols)
            {
                return;
            }

            if (!TryGetDocumentSymbolIdentity(
                node,
                out string name,
                out LspSymbolKind kind,
                out TextSpan selectionSpan))
            {
                AddDocumentSymbols(
                    node.ChildNodes(),
                    target,
                    semanticModel,
                    text,
                    ref symbolCount,
                    cancellationToken);
                continue;
            }

            symbolCount++;
            var children = new List<DocumentSymbol>();
            AddDocumentSymbols(
                node.ChildNodes(),
                children,
                semanticModel,
                text,
                ref symbolCount,
                cancellationToken);
            ISymbol? declaredSymbol = semanticModel.GetDeclaredSymbol(node, cancellationToken);
            target.Add(new DocumentSymbol
            {
                Name = name,
                Detail = declaredSymbol?.ToDisplayString(
                    SymbolDisplayFormat.MinimallyQualifiedFormat),
                Kind = kind,
                Range = ToLspRange(text, node.Span),
                SelectionRange = ToLspRange(text, selectionSpan),
                Children = children.Count == 0 ? null : children
            });
        }
    }

    private static bool TryGetDocumentSymbolIdentity(
        SyntaxNode node,
        out string name,
        out LspSymbolKind kind,
        out TextSpan selectionSpan)
    {
        switch (node)
        {
            case BaseNamespaceDeclarationSyntax namespaceDeclaration:
                name = namespaceDeclaration.Name.ToString();
                kind = LspSymbolKind.Namespace;
                selectionSpan = namespaceDeclaration.Name.Span;
                return true;
            case ClassDeclarationSyntax classDeclaration:
                name = classDeclaration.Identifier.ValueText;
                kind = LspSymbolKind.Class;
                selectionSpan = classDeclaration.Identifier.Span;
                return true;
            case StructDeclarationSyntax structDeclaration:
                name = structDeclaration.Identifier.ValueText;
                kind = LspSymbolKind.Struct;
                selectionSpan = structDeclaration.Identifier.Span;
                return true;
            case InterfaceDeclarationSyntax interfaceDeclaration:
                name = interfaceDeclaration.Identifier.ValueText;
                kind = LspSymbolKind.Interface;
                selectionSpan = interfaceDeclaration.Identifier.Span;
                return true;
            case RecordDeclarationSyntax recordDeclaration:
                name = recordDeclaration.Identifier.ValueText;
                kind = recordDeclaration.ClassOrStructKeyword.IsKind(SyntaxKind.StructKeyword)
                    ? LspSymbolKind.Struct
                    : LspSymbolKind.Class;
                selectionSpan = recordDeclaration.Identifier.Span;
                return true;
            case EnumDeclarationSyntax enumDeclaration:
                name = enumDeclaration.Identifier.ValueText;
                kind = LspSymbolKind.Enum;
                selectionSpan = enumDeclaration.Identifier.Span;
                return true;
            case DelegateDeclarationSyntax delegateDeclaration:
                name = delegateDeclaration.Identifier.ValueText;
                kind = LspSymbolKind.Function;
                selectionSpan = delegateDeclaration.Identifier.Span;
                return true;
            case MethodDeclarationSyntax methodDeclaration:
                name = methodDeclaration.Identifier.ValueText;
                kind = LspSymbolKind.Method;
                selectionSpan = methodDeclaration.Identifier.Span;
                return true;
            case ConstructorDeclarationSyntax constructorDeclaration:
                name = constructorDeclaration.Identifier.ValueText;
                kind = LspSymbolKind.Constructor;
                selectionSpan = constructorDeclaration.Identifier.Span;
                return true;
            case DestructorDeclarationSyntax destructorDeclaration:
                name = "~" + destructorDeclaration.Identifier.ValueText;
                kind = LspSymbolKind.Constructor;
                selectionSpan = destructorDeclaration.Identifier.Span;
                return true;
            case OperatorDeclarationSyntax operatorDeclaration:
                name = "operator " + operatorDeclaration.OperatorToken.ValueText;
                kind = LspSymbolKind.Operator;
                selectionSpan = operatorDeclaration.OperatorToken.Span;
                return true;
            case ConversionOperatorDeclarationSyntax conversionDeclaration:
                name = "operator " + conversionDeclaration.Type;
                kind = LspSymbolKind.Operator;
                selectionSpan = conversionDeclaration.Type.Span;
                return true;
            case PropertyDeclarationSyntax propertyDeclaration:
                name = propertyDeclaration.Identifier.ValueText;
                kind = LspSymbolKind.Property;
                selectionSpan = propertyDeclaration.Identifier.Span;
                return true;
            case IndexerDeclarationSyntax indexerDeclaration:
                name = "this";
                kind = LspSymbolKind.Property;
                selectionSpan = indexerDeclaration.ThisKeyword.Span;
                return true;
            case EventDeclarationSyntax eventDeclaration:
                name = eventDeclaration.Identifier.ValueText;
                kind = LspSymbolKind.Event;
                selectionSpan = eventDeclaration.Identifier.Span;
                return true;
            case EnumMemberDeclarationSyntax enumMember:
                name = enumMember.Identifier.ValueText;
                kind = LspSymbolKind.EnumMember;
                selectionSpan = enumMember.Identifier.Span;
                return true;
            case LocalFunctionStatementSyntax localFunction:
                name = localFunction.Identifier.ValueText;
                kind = LspSymbolKind.Function;
                selectionSpan = localFunction.Identifier.Span;
                return true;
            case VariableDeclaratorSyntax variable when
                variable.Parent?.Parent is FieldDeclarationSyntax field:
                name = variable.Identifier.ValueText;
                kind = field.Modifiers.Any(SyntaxKind.ConstKeyword)
                    ? LspSymbolKind.Constant
                    : LspSymbolKind.Field;
                selectionSpan = variable.Identifier.Span;
                return true;
            case VariableDeclaratorSyntax variable when
                variable.Parent?.Parent is EventFieldDeclarationSyntax:
                name = variable.Identifier.ValueText;
                kind = LspSymbolKind.Event;
                selectionSpan = variable.Identifier.Span;
                return true;
            default:
                name = string.Empty;
                kind = default;
                selectionSpan = default;
                return false;
        }
    }

    private static LspRange ToLspRange(SourceText text, TextSpan span)
    {
        LinePositionSpan lineSpan = text.Lines.GetLinePositionSpan(span);
        return new LspRange(
            new Position(lineSpan.Start.Line, lineSpan.Start.Character),
            new Position(lineSpan.End.Line, lineSpan.End.Character));
    }

    private static LspSymbolKind GetSymbolKind(ISymbol symbol) => symbol switch
    {
        INamespaceSymbol => LspSymbolKind.Namespace,
        INamedTypeSymbol { TypeKind: TypeKind.Class or TypeKind.Submission } =>
            LspSymbolKind.Class,
        INamedTypeSymbol { TypeKind: TypeKind.Struct } => LspSymbolKind.Struct,
        INamedTypeSymbol { TypeKind: TypeKind.Interface } => LspSymbolKind.Interface,
        INamedTypeSymbol { TypeKind: TypeKind.Enum } => LspSymbolKind.Enum,
        INamedTypeSymbol { TypeKind: TypeKind.Delegate } => LspSymbolKind.Function,
        IMethodSymbol { MethodKind: MethodKind.Constructor or MethodKind.StaticConstructor } =>
            LspSymbolKind.Constructor,
        IMethodSymbol
        {
            MethodKind: MethodKind.UserDefinedOperator or MethodKind.Conversion
        } => LspSymbolKind.Operator,
        IMethodSymbol { MethodKind: MethodKind.LocalFunction } => LspSymbolKind.Function,
        IMethodSymbol => LspSymbolKind.Method,
        IPropertySymbol => LspSymbolKind.Property,
        IFieldSymbol { ContainingType.TypeKind: TypeKind.Enum } => LspSymbolKind.EnumMember,
        IFieldSymbol { IsConst: true } => LspSymbolKind.Constant,
        IFieldSymbol => LspSymbolKind.Field,
        IEventSymbol => LspSymbolKind.Event,
        ITypeParameterSymbol => LspSymbolKind.TypeParameter,
        ILocalSymbol or IParameterSymbol or IRangeVariableSymbol => LspSymbolKind.Variable,
        _ => LspSymbolKind.ObjectValue
    };

    private static string? GetContainerName(ISymbol symbol)
    {
        ISymbol? container = symbol.ContainingType ?? (ISymbol?)symbol.ContainingNamespace;
        return container is null ||
            container is INamespaceSymbol { IsGlobalNamespace: true }
                ? null
                : container.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat);
    }

    private static int GetWorkspaceSymbolMatchRank(string name, string query)
    {
        if (query.Length == 0 || string.Equals(name, query, StringComparison.OrdinalIgnoreCase))
        {
            return 0;
        }

        if (name.StartsWith(query, StringComparison.OrdinalIgnoreCase))
        {
            return 1;
        }

        return name.Contains(query, StringComparison.OrdinalIgnoreCase) ? 2 : 3;
    }

    private static BaseArgumentListSyntax? FindArgumentList(SyntaxNode root, int offset)
    {
        int tokenOffset = Math.Clamp(offset == root.FullSpan.End ? offset - 1 : offset, 0,
            Math.Max(0, root.FullSpan.End - 1));
        SyntaxToken token = root.FindToken(tokenOffset, findInsideTrivia: true);
        return token.Parent?
            .AncestorsAndSelf()
            .OfType<BaseArgumentListSyntax>()
            .FirstOrDefault(argumentList =>
                argumentList.SpanStart < offset && offset <= argumentList.Span.End);
    }

    private static (IReadOnlyList<IMethodSymbol> Methods, IMethodSymbol? SelectedMethod)
        GetSignatureMethods(
            BaseArgumentListSyntax argumentList,
            SemanticModel semanticModel,
            CancellationToken cancellationToken)
    {
        IEnumerable<IMethodSymbol> candidates;
        SymbolInfo symbolInfo;
        switch (argumentList.Parent)
        {
            case InvocationExpressionSyntax invocation:
                symbolInfo = semanticModel.GetSymbolInfo(invocation, cancellationToken);
                candidates = semanticModel
                    .GetMemberGroup(invocation.Expression, cancellationToken)
                    .OfType<IMethodSymbol>();
                break;
            case ObjectCreationExpressionSyntax objectCreation:
                symbolInfo = semanticModel.GetSymbolInfo(objectCreation, cancellationToken);
                candidates = semanticModel.GetTypeInfo(objectCreation.Type, cancellationToken).Type
                    is INamedTypeSymbol namedType
                        ? namedType.InstanceConstructors
                        : [];
                break;
            case ConstructorInitializerSyntax constructorInitializer:
                symbolInfo = semanticModel.GetSymbolInfo(
                    constructorInitializer,
                    cancellationToken);
                candidates = [];
                break;
            default:
                return ([], null);
        }

        var methods = new List<IMethodSymbol>();
        var seenMethods = new HashSet<IMethodSymbol>(SymbolEqualityComparer.Default);
        foreach (IMethodSymbol method in candidates)
        {
            AddSignatureMethod(methods, seenMethods, method);
        }

        foreach (ISymbol candidateSymbol in symbolInfo.CandidateSymbols)
        {
            AddSignatureMethod(methods, seenMethods, candidateSymbol);
        }

        AddSignatureMethod(methods, seenMethods, symbolInfo.Symbol);

        methods.Sort(static (left, right) =>
        {
            int parameterComparison = left.Parameters.Length.CompareTo(right.Parameters.Length);
            return parameterComparison != 0
                ? parameterComparison
                : StringComparer.Ordinal.Compare(
                    left.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),
                    right.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat));
        });
        return (methods, symbolInfo.Symbol as IMethodSymbol);
    }

    private static void AddSignatureMethod(
        List<IMethodSymbol> methods,
        HashSet<IMethodSymbol> seenMethods,
        ISymbol? symbol)
    {
        if (symbol is IMethodSymbol method && seenMethods.Add(method))
        {
            methods.Add(method);
        }
    }

    private static int FindBestSignature(IReadOnlyList<IMethodSymbol> methods, int activeParameter)
    {
        for (int index = 0; index < methods.Count; index++)
        {
            IMethodSymbol method = methods[index];
            if (method.Parameters.Length > activeParameter ||
                method.Parameters is [.., { IsParams: true }])
            {
                return index;
            }
        }

        return 0;
    }

    private static int FindSignatureIndex(
        IReadOnlyList<IMethodSymbol> methods,
        IMethodSymbol selectedMethod)
    {
        for (int index = 0; index < methods.Count; index++)
        {
            if (SymbolEqualityComparer.Default.Equals(methods[index], selectedMethod) ||
                SymbolEqualityComparer.Default.Equals(
                    methods[index].OriginalDefinition,
                    selectedMethod.OriginalDefinition))
            {
                return index;
            }
        }

        return 0;
    }

    private static SignatureInformation CreateSignatureInformation(
        IMethodSymbol method,
        int activeParameter)
    {
        ParameterInformation[] parameters =
        [
            .. method.Parameters.Select(static parameter => new ParameterInformation
            {
                Label = parameter.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)
            })
        ];
        return new SignatureInformation
        {
            Label = method.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),
            Parameters = parameters,
            ActiveParameter = parameters.Length == 0
                ? null
                : Math.Min(activeParameter, parameters.Length - 1)
        };
    }

    private Document? FindDocument(DocumentUri uri)
    {
        string path = uri.GetFileSystemPath();
        ImmutableArray<(string RootPath, Workspace Workspace, Solution Solution)> folders = _folders;
        int folderIndex = FindFolderIndex(path, folders);
        return folderIndex >= 0 ? FindDocument(folders[folderIndex].Solution, path) : null;
    }

    private TextDocument? FindTextDocument(DocumentUri uri)
    {
        string path = uri.GetFileSystemPath();
        ImmutableArray<(string RootPath, Workspace Workspace, Solution Solution)> folders = _folders;
        int folderIndex = FindFolderIndex(path, folders);
        return folderIndex >= 0
            ? FindTextDocument(folders[folderIndex].Solution, path)
            : null;
    }

    private static ISymbol? NormalizeRenameSymbol(ISymbol? symbol) => symbol switch
    {
        IMethodSymbol
        {
            MethodKind: MethodKind.Constructor or MethodKind.StaticConstructor,
            ContainingType: { } containingType
        } => containingType,
        _ => symbol
    };

    private static bool CanRenameSymbol(ISymbol? symbol) =>
        symbol is
        {
            IsImplicitlyDeclared: false,
            Kind: RoslynSymbolKind.Alias or
                RoslynSymbolKind.Event or
                RoslynSymbolKind.Field or
                RoslynSymbolKind.Label or
                RoslynSymbolKind.Local or
                RoslynSymbolKind.Method or
                RoslynSymbolKind.NamedType or
                RoslynSymbolKind.Namespace or
                RoslynSymbolKind.Parameter or
                RoslynSymbolKind.Property or
                RoslynSymbolKind.RangeVariable or
                RoslynSymbolKind.TypeParameter
        } &&
        symbol.Locations.Any(static location => location.IsInSource);

    private Task<WorkspaceEdit> CreateWorkspaceEditAsync(
        Solution originalSolution,
        Solution changedSolution,
        CancellationToken cancellationToken) =>
        CreateWorkspaceEditAsync(
            originalSolution,
            changedSolution,
            additionalEdits: null,
            cancellationToken);

    private async Task<WorkspaceEdit> CreateWorkspaceEditAsync(
        Solution originalSolution,
        Solution changedSolution,
        IReadOnlyDictionary<string, IReadOnlyList<LspTextEdit>>? additionalEdits,
        CancellationToken cancellationToken)
    {
        var editsByPath = new Dictionary<string, TextDocumentEdit>(PathComparer);
        int totalEditCount = 0;
        foreach (ProjectChanges projectChanges in changedSolution
            .GetChanges(originalSolution)
            .GetProjectChanges())
        {
            foreach (DocumentId documentId in projectChanges.GetChangedDocuments())
            {
                cancellationToken.ThrowIfCancellationRequested();
                Document? originalDocument = originalSolution.GetDocument(documentId);
                Document? changedDocument = changedSolution.GetDocument(documentId);
                string? path = changedDocument?.FilePath ?? originalDocument?.FilePath;
                if (originalDocument is null ||
                    changedDocument is null ||
                    string.IsNullOrWhiteSpace(path))
                {
                    continue;
                }

                SourceText originalText = await originalDocument
                    .GetTextAsync(cancellationToken)
                    .ConfigureAwait(false);
                IReadOnlyList<LspTextEdit> documentEdits = CreateTextEdits(
                    originalText,
                    await changedDocument
                        .GetTextChangesAsync(originalDocument, cancellationToken)
                        .ConfigureAwait(false));
                if (documentEdits.Count == 0)
                {
                    continue;
                }

                totalEditCount += documentEdits.Count;
                if (totalEditCount > MaximumWorkspaceTextEdits)
                {
                    throw new InvalidOperationException(
                        $"The workspace edit exceeds {MaximumWorkspaceTextEdits} text edits.");
                }

                var documentEdit = new TextDocumentEdit
                {
                    TextDocument = new OptionalVersionedTextDocumentIdentifier
                    {
                        Uri = DocumentUri.FromFileSystemPath(path),
                        Version = _documentVersions.TryGetValue(path, out int version)
                            ? version
                            : null
                    },
                    Edits = documentEdits
                };
                if (editsByPath.TryGetValue(path, out TextDocumentEdit? existingEdit) &&
                    !existingEdit.Edits.SequenceEqual(documentEdit.Edits))
                {
                    throw new InvalidOperationException(
                        $"Linked document {path} produced conflicting workspace edits.");
                }

                editsByPath[path] = documentEdit;
            }
        }

        if (additionalEdits is not null)
        {
            foreach ((string path, IReadOnlyList<LspTextEdit> edits) in additionalEdits)
            {
                if (edits.Count == 0)
                {
                    continue;
                }

                if (editsByPath.TryGetValue(path, out TextDocumentEdit? existingEdit))
                {
                    editsByPath[path] = new TextDocumentEdit
                    {
                        TextDocument = existingEdit.TextDocument,
                        Edits = MergeTextEdits(existingEdit.Edits, edits, path)
                    };
                    continue;
                }

                editsByPath[path] = new TextDocumentEdit
                {
                    TextDocument = new OptionalVersionedTextDocumentIdentifier
                    {
                        Uri = DocumentUri.FromFileSystemPath(path),
                        Version = _documentVersions.TryGetValue(path, out int version)
                            ? version
                            : null
                    },
                    Edits = MergeTextEdits([], edits, path)
                };
            }
        }

        totalEditCount = editsByPath.Sum(static pair => pair.Value.Edits.Count);
        if (totalEditCount > MaximumWorkspaceTextEdits)
        {
            throw new InvalidOperationException(
                $"The workspace edit exceeds {MaximumWorkspaceTextEdits} text edits.");
        }

        return new WorkspaceEdit
        {
            DocumentChanges =
            [
                .. editsByPath
                    .OrderBy(static pair => pair.Key, PathComparer)
                    .Select(static pair => pair.Value)
            ]
        };
    }

    private static LspTextEdit[] MergeTextEdits(
        IReadOnlyList<LspTextEdit> existingEdits,
        IReadOnlyList<LspTextEdit> additionalEdits,
        string path)
    {
        LspTextEdit[] edits =
        [
            .. existingEdits
                .Concat(additionalEdits)
                .Distinct()
                .OrderBy(static edit => edit.Range.Start.Line)
                .ThenBy(static edit => edit.Range.Start.Character)
                .ThenBy(static edit => edit.Range.End.Line)
                .ThenBy(static edit => edit.Range.End.Character)
        ];
        for (int index = 1; index < edits.Length; index++)
        {
            if (ComparePositions(edits[index - 1].Range.End, edits[index].Range.Start) > 0)
            {
                throw new InvalidOperationException(
                    $"Razor document {path} produced overlapping workspace edits.");
            }
        }

        return edits;
    }

    private static int ComparePositions(Position left, Position right)
    {
        int lineComparison = left.Line.CompareTo(right.Line);
        return lineComparison != 0
            ? lineComparison
            : left.Character.CompareTo(right.Character);
    }

    private static IReadOnlyList<LspTextEdit> CreateTextEdits(
        SourceText originalText,
        IEnumerable<TextChange> changes)
    {
        TextChange[] orderedChanges =
        [
            .. changes.OrderBy(static change => change.Span.Start)
        ];
        if (orderedChanges.Length > MaximumWorkspaceTextEdits)
        {
            throw new InvalidOperationException(
                $"The document edit exceeds {MaximumWorkspaceTextEdits} text edits.");
        }

        return
        [
            .. orderedChanges.Select(change => new LspTextEdit
            {
                Range = ToLspRange(originalText, change.Span),
                NewText = change.NewText ?? string.Empty
            })
        ];
    }

    private static bool IsCodeActionRequested(
        IReadOnlyList<string>? requestedKinds,
        string candidateKind) =>
        requestedKinds is not { Count: > 0 } ||
        requestedKinds.Any(requestedKind =>
            string.Equals(candidateKind, requestedKind, StringComparison.Ordinal) ||
            candidateKind.StartsWith(requestedKind + '.', StringComparison.Ordinal));

    private static string ComputeTextHash(string text) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    private static SourceText ApplyTextEdits(
        SourceText originalText,
        IReadOnlyList<LspTextEdit> edits)
    {
        if (edits.Count > MaximumWorkspaceTextEdits)
        {
            throw new InvalidDataException(
                $"The edit plan exceeds {MaximumWorkspaceTextEdits} edits in one document.");
        }

        TextChange[] changes =
        [
            .. edits
                .Select(edit =>
                {
                    int start = LspPositionConverter.GetOffset(originalText, edit.Range.Start);
                    int end = LspPositionConverter.GetOffset(originalText, edit.Range.End);
                    return new TextChange(
                        TextSpan.FromBounds(start, end),
                        edit.NewText);
                })
                .OrderBy(static change => change.Span.Start)
                .ThenBy(static change => change.Span.End)
        ];
        int priorEnd = 0;
        foreach (TextChange change in changes)
        {
            if (change.Span.Start < priorEnd)
            {
                throw new InvalidDataException("The edit plan contains overlapping text edits.");
            }

            priorEnd = change.Span.End;
        }

        return originalText.WithChanges(changes);
    }

    private void ReplaceStagedFiles(
        Dictionary<string, (string TempPath, string BackupPath, SourceText Text)> stagedFiles)
    {
        var replacedPaths = new List<string>(stagedFiles.Count);
        try
        {
            foreach ((string path, (string tempPath, string backupPath, SourceText _)) in
                stagedFiles)
            {
                File.Replace(tempPath, path, backupPath);
                replacedPaths.Add(path);
            }
        }
        catch (Exception replacementException) when (
            replacementException is IOException or UnauthorizedAccessException)
        {
            var rollbackExceptions = new List<Exception>();
            for (int index = replacedPaths.Count - 1; index >= 0; index--)
            {
                string path = replacedPaths[index];
                string backupPath = stagedFiles[path].BackupPath;
                try
                {
                    if (File.Exists(backupPath))
                    {
                        File.Move(backupPath, path, overwrite: true);
                    }
                }
                catch (Exception rollbackException) when (
                    rollbackException is IOException or UnauthorizedAccessException)
                {
                    rollbackExceptions.Add(rollbackException);
                }
            }

            if (rollbackExceptions.Count > 0)
            {
                rollbackExceptions.Insert(0, replacementException);
                throw new AggregateException(
                    "Applying the edit failed and at least one document could not be restored.",
                    rollbackExceptions);
            }

            throw;
        }

        foreach ((string _, (string _, string backupPath, SourceText _)) in stagedFiles)
        {
            try
            {
                File.Delete(backupPath);
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException)
            {
                LogEditArtifactCleanupFailure(backupPath, exception);
            }
        }
    }

    private void PublishAppliedTexts(
        Dictionary<string, (string TempPath, string BackupPath, SourceText Text)> stagedFiles)
    {
        for (int folderIndex = 0; folderIndex < _folders.Length; folderIndex++)
        {
            (string rootPath, Workspace workspace, Solution solution) = _folders[folderIndex];
            foreach ((string path, (string _, string _, SourceText text)) in stagedFiles)
            {
                DocumentId[] documentIds =
                [
                    .. solution.Projects
                        .SelectMany(static project => project.Documents)
                        .Where(document => string.Equals(
                            document.FilePath,
                            path,
                            PathComparison))
                        .Select(static document => document.Id)
                ];
                foreach (DocumentId documentId in documentIds)
                {
                    solution = solution.WithDocumentText(
                        documentId,
                        text,
                        PreservationMode.PreserveIdentity);
                }

                solution = WithAdditionalDocumentText(solution, path, text);
            }

            _folders = _folders.SetItem(folderIndex, (rootPath, workspace, solution));
        }
    }

    private void CleanupStagedFiles(
        Dictionary<string, (string TempPath, string BackupPath, SourceText Text)> stagedFiles)
    {
        foreach ((string _, (string tempPath, string _, SourceText _)) in stagedFiles)
        {
            try
            {
                File.Delete(tempPath);
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException)
            {
                LogEditArtifactCleanupFailure(tempPath, exception);
            }
        }
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
                throw new InvalidOperationException(
                    $"Roslyn rejected loose files under {rootPath}.");
            }

            return (workspace, workspace.CurrentSolution);
        }
        catch
        {
            workspace.Dispose();
            throw;
        }
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
            string containmentRoot = IsWorkspaceEntryPoint(rootPath)
                ? Path.GetDirectoryName(rootPath) ?? rootPath
                : rootPath;
            bool ownsPath = string.Equals(documentPath, rootPath, PathComparison) ||
                documentPath.StartsWith(
                    containmentRoot + Path.DirectorySeparatorChar,
                    PathComparison);
            if (!ownsPath)
            {
                continue;
            }

            bool containsDocument = FindDocument(folders[index].Solution, documentPath) is not null ||
                folders[index].Solution.Projects.Any(project =>
                    ContainsAdditionalDocument(project, documentPath));
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

    private static bool IsWorkspaceEntryPoint(string path)
    {
        string extension = Path.GetExtension(path);
        return extension.Equals(".slnx", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".sln", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".csproj", StringComparison.OrdinalIgnoreCase) ||
            WorkspaceDiscovery.IsFileBasedApp(path);
    }

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private static Document? FindDocument(Solution solution, string path)
    {
        return solution.Projects
            .SelectMany(static project => project.Documents)
            .FirstOrDefault(document => string.Equals(document.FilePath, path, PathComparison));
    }

    private static TextDocument? FindTextDocument(Solution solution, string path)
    {
        ImmutableArray<DocumentId> documentIds = solution.GetDocumentIdsWithFilePath(path);
        for (int index = 0; index < documentIds.Length; index++)
        {
            DocumentId documentId = documentIds[index];
            TextDocument? document = solution.GetDocument(documentId) ??
                solution.GetAdditionalDocument(documentId);
            if (document is not null)
            {
                return document;
            }
        }

        return null;
    }

    private static bool ContainsAdditionalDocument(Project project, string path)
    {
        Solution solution = project.Solution;
        ImmutableArray<DocumentId> documentIds = solution.GetDocumentIdsWithFilePath(path);
        for (int index = 0; index < documentIds.Length; index++)
        {
            DocumentId documentId = documentIds[index];
            if (documentId.ProjectId == project.Id &&
                solution.GetAdditionalDocument(documentId) is not null)
            {
                return true;
            }
        }

        return false;
    }

    private static Solution WithAdditionalDocumentText(
        Solution solution,
        string path,
        SourceText text)
    {
        ImmutableArray<DocumentId> documentIds = solution.GetDocumentIdsWithFilePath(path);
        for (int index = 0; index < documentIds.Length; index++)
        {
            DocumentId documentId = documentIds[index];
            if (solution.GetAdditionalDocument(documentId) is not null)
            {
                solution = solution.WithAdditionalDocumentText(
                    documentId,
                    text,
                    PreservationMode.PreserveIdentity);
            }
        }

        return solution;
    }

    private static async Task<Solution> RestoreAdditionalDocumentTextAsync(
        Solution solution,
        string path,
        CancellationToken cancellationToken)
    {
        ImmutableArray<DocumentId> documentIds = solution.GetDocumentIdsWithFilePath(path);
        bool containsAdditionalDocument = false;
        for (int index = 0; index < documentIds.Length; index++)
        {
            if (solution.GetAdditionalDocument(documentIds[index]) is not null)
            {
                containsAdditionalDocument = true;
                break;
            }
        }

        if (!containsAdditionalDocument)
        {
            return solution;
        }

        if (File.Exists(path))
        {
            string persistedText = await File.ReadAllTextAsync(path, cancellationToken)
                .ConfigureAwait(false);
            var text = SourceText.From(persistedText, Encoding.UTF8);
            for (int index = 0; index < documentIds.Length; index++)
            {
                DocumentId documentId = documentIds[index];
                if (solution.GetAdditionalDocument(documentId) is not null)
                {
                    solution = solution.WithAdditionalDocumentText(
                        documentId,
                        text,
                        PreservationMode.PreserveIdentity);
                }
            }

            return solution;
        }

        for (int index = 0; index < documentIds.Length; index++)
        {
            DocumentId documentId = documentIds[index];
            if (solution.GetAdditionalDocument(documentId) is not null)
            {
                solution = solution.RemoveAdditionalDocument(documentId);
            }
        }

        return solution;
    }

    private async Task<ImmutableArray<RoslynDiagnostic>>
        ComputeProjectDiagnosticsAsync(Project project, CancellationToken cancellationToken)
    {
        Compilation compilation = await project
            .GetCompilationAsync(cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException(
                $"Roslyn returned no compilation for project {project.Name}.");
        ImmutableArray<RoslynDiagnostic> compilerDiagnostics =
            compilation.GetDiagnostics(cancellationToken);
        if (Volatile.Read(ref _enableAnalyzers) == 0)
        {
            return compilerDiagnostics;
        }

        ImmutableArray<DiagnosticAnalyzer> analyzers =
        [
            .. project.AnalyzerReferences
                .SelectMany(reference => reference.GetAnalyzers(project.Language))
        ];
        if (analyzers.IsDefaultOrEmpty)
        {
            return compilerDiagnostics;
        }

        CompilationWithAnalyzers compilationWithAnalyzers = compilation.WithAnalyzers(
            analyzers,
            project.AnalyzerOptions);
        ImmutableArray<RoslynDiagnostic> analyzerDiagnostics =
            await compilationWithAnalyzers
                .GetAnalyzerDiagnosticsAsync(cancellationToken)
                .ConfigureAwait(false);
        return compilerDiagnostics.AddRange(analyzerDiagnostics);
    }

    private static bool IsDocumentDiagnostic(
        RoslynDiagnostic diagnostic,
        string path)
    {
        return !diagnostic.IsSuppressed &&
            diagnostic.Location.IsInSource &&
            string.Equals(
                diagnostic.Location.SourceTree?.FilePath,
                path,
                PathComparison);
    }

    private static SourceText ApplyContentChanges(
        SourceText text,
        IReadOnlyList<TextDocumentContentChangeEvent> changes,
        CancellationToken cancellationToken)
    {
        foreach (TextDocumentContentChangeEvent change in changes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (change.Range is not LspRange range)
            {
                text = SourceText.From(change.Text, Encoding.UTF8);
                continue;
            }

            int start = LspPositionConverter.GetOffset(text, range.Start);
            int end = LspPositionConverter.GetOffset(text, range.End);
            int replacedLength = end - start;
            if (change.RangeLength is int expectedLength && expectedLength != replacedLength)
            {
                throw new InvalidDataException(
                    $"Change range length {expectedLength} does not match {replacedLength} UTF-16 code units.");
            }

            text = text.WithChanges(new TextChange(
                new TextSpan(start, replacedLength),
                change.Text));
        }

        return text;
    }

    private static LspDiagnostic ToLspDiagnostic(
        RoslynDiagnostic diagnostic)
    {
        FileLinePositionSpan lineSpan = diagnostic.Location.GetLineSpan();
        return new LspDiagnostic
        {
            Range = new LspRange(
                new Position(lineSpan.StartLinePosition.Line, lineSpan.StartLinePosition.Character),
                new Position(lineSpan.EndLinePosition.Line, lineSpan.EndLinePosition.Character)),
            Severity = diagnostic.Severity switch
            {
                RoslynDiagnosticSeverity.Error => LspDiagnosticSeverity.Error,
                RoslynDiagnosticSeverity.Warning => LspDiagnosticSeverity.Warning,
                RoslynDiagnosticSeverity.Info => LspDiagnosticSeverity.Information,
                RoslynDiagnosticSeverity.Hidden => LspDiagnosticSeverity.Hint,
                _ => null
            },
            Code = diagnostic.Id,
            Source = diagnostic.Id.StartsWith("CS", StringComparison.Ordinal)
                ? "C#"
                : diagnostic.Descriptor.Category,
            Message = diagnostic.GetMessage(CultureInfo.InvariantCulture)
        };
    }

    private async Task<(Document? Document, ISymbol? Symbol)> FindSymbolAsync(
        TextDocumentPositionParams parameters,
        CancellationToken cancellationToken)
    {
        string path = parameters.TextDocument.Uri.GetFileSystemPath();
        ImmutableArray<(string RootPath, Workspace Workspace, Solution Solution)> folders = _folders;
        int folderIndex = FindFolderIndex(path, folders);
        Document? document = folderIndex >= 0
            ? FindDocument(folders[folderIndex].Solution, path)
            : null;
        if (document is null)
        {
            return (null, null);
        }

        SourceText text = await document.GetTextAsync(cancellationToken).ConfigureAwait(false);
        int offset = LspPositionConverter.GetOffset(text, parameters.Position);
        SemanticModel semanticModel = await document
            .GetSemanticModelAsync(cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException("Roslyn returned no semantic model.");
        ISymbol? symbol = await SymbolFinder.FindSymbolAtPositionAsync(
            semanticModel,
            offset,
            folders[folderIndex].Workspace,
            cancellationToken).ConfigureAwait(false);
        return (document, symbol);
    }

    private static async Task<(Document? Document, ISymbol? Symbol)>
        ResolveRazorRenameSymbolAsync(
            Solution solution,
            string path,
            Position position,
            CancellationToken cancellationToken)
    {
        (RazorMappedDocument? mappedDocument, ISymbol? symbol, LspRange? _) =
            await WorkspaceRazorRenameService.ResolveTargetAsync(
                solution,
                path,
                position,
                cancellationToken).ConfigureAwait(false);
        return (mappedDocument?.Document, symbol);
    }

    private Document? FindCurrentDocument(DocumentUri uri)
    {
        string path = uri.GetFileSystemPath();
        ImmutableArray<(string RootPath, Workspace Workspace, Solution Solution)> folders = _folders;
        int folderIndex = FindFolderIndex(path, folders);
        return folderIndex >= 0
            ? FindDocument(folders[folderIndex].Solution, path)
            : null;
    }

    private static LspLocation? ToLspLocation(RoslynLocation location)
    {
        string? path = location.SourceTree?.FilePath;
        if (!location.IsInSource || string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        FileLinePositionSpan lineSpan = location.GetLineSpan();
        return new LspLocation
        {
            Uri = DocumentUri.FromFileSystemPath(path),
            Range = new LspRange(
                new Position(lineSpan.StartLinePosition.Line, lineSpan.StartLinePosition.Character),
                new Position(lineSpan.EndLinePosition.Line, lineSpan.EndLinePosition.Character))
        };
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

    [LoggerMessage(
        EventId = 3,
        Level = LogLevel.Warning,
        Message = "Could not remove edit transaction artifact {Path}")]
    private partial void LogEditArtifactCleanupFailure(string path, Exception exception);
}
