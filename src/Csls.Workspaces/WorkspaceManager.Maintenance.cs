using Csls.Protocol;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using System.Collections.Immutable;
using System.Text;

namespace Csls.Workspaces;

public sealed partial class WorkspaceManager
{
    /// <summary>
    /// Applies diagnostic settings and invalidates results affected by a real change.
    /// </summary>
    /// <param name="enableAnalyzers">Whether project analyzers contribute diagnostics.</param>
    /// <param name="cancellationToken">The operation cancellation token.</param>
    /// <returns>True when the effective configuration changed.</returns>
    public async Task<bool> ConfigureAsync(
        bool enableAnalyzers,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposeState) != 0, this);
        await _mutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            int value = enableAnalyzers ? 1 : 0;
            if (Volatile.Read(ref _enableAnalyzers) == value)
            {
                return false;
            }

            Volatile.Write(ref _enableAnalyzers, value);
            _diagnosticCache.Clear();
            Interlocked.Increment(ref _generation);
            return true;
        }
        finally
        {
            _mutationGate.Release();
        }
    }

    /// <summary>
    /// Reloads every current workspace root while preserving unsaved client document overlays.
    /// </summary>
    /// <param name="cancellationToken">The operation cancellation token.</param>
    /// <returns>The completed workspace maintenance result.</returns>
    public Task<WorkspaceMaintenanceResult> ReloadAsync(CancellationToken cancellationToken) =>
        ReloadAsync(restoredEntryPointCount: 0, cancellationToken);

    /// <summary>
    /// Replaces workspace roots while preserving overlays still owned by the new root set.
    /// </summary>
    /// <param name="rootPaths">The complete ordered workspace root set.</param>
    /// <param name="cancellationToken">The operation cancellation token.</param>
    /// <returns>The completed workspace maintenance result.</returns>
    public Task<WorkspaceMaintenanceResult> ChangeWorkspaceFoldersAsync(
        IReadOnlyList<string> rootPaths,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(rootPaths);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposeState) != 0, this);
        return ReloadAsync(rootPaths, restoredEntryPointCount: 0, cancellationToken);
    }

    /// <summary>
    /// Restores every current workspace entry point and reloads the resulting Roslyn state.
    /// </summary>
    /// <param name="cancellationToken">The operation cancellation token.</param>
    /// <returns>The completed workspace maintenance result.</returns>
    public async Task<WorkspaceMaintenanceResult> RestoreAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<string> roots = GetRequiredWorkspaceRoots();
        int restoredEntryPointCount = await DotNetWorkspaceRestorer
            .RestoreAsync(roots, cancellationToken)
            .ConfigureAwait(false);
        return await ReloadAsync(restoredEntryPointCount, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Recreates every Roslyn workspace host while preserving unsaved client document overlays.
    /// </summary>
    /// <param name="cancellationToken">The operation cancellation token.</param>
    /// <returns>The completed workspace maintenance result.</returns>
    public async Task<WorkspaceMaintenanceResult> RestartBuildHostsAsync(
        CancellationToken cancellationToken)
    {
        WorkspaceMaintenanceResult result = await ReloadAsync(cancellationToken)
            .ConfigureAwait(false);
        return new WorkspaceMaintenanceResult
        {
            PreviousGeneration = result.PreviousGeneration,
            CurrentGeneration = result.CurrentGeneration,
            AffectedWorkspaceCount = result.AffectedWorkspaceCount,
            RestartedBuildHostCount = result.AffectedWorkspaceCount,
            ClearedCacheEntryCount = result.ClearedCacheEntryCount
        };
    }

    /// <summary>
    /// Removes every retained project diagnostic result from the current workspace.
    /// </summary>
    /// <returns>The number of removed diagnostic cache entries.</returns>
    public int ClearCaches()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposeState) != 0, this);
        int clearedEntryCount = _diagnosticCache.Count;
        _diagnosticCache.Clear();
        return clearedEntryCount;
    }

    private Task<WorkspaceMaintenanceResult> ReloadAsync(
        int restoredEntryPointCount,
        CancellationToken cancellationToken) =>
        ReloadAsync(
            GetRequiredWorkspaceRoots(),
            restoredEntryPointCount,
            cancellationToken);

    private async Task<WorkspaceMaintenanceResult> ReloadAsync(
        IReadOnlyList<string> roots,
        int restoredEntryPointCount,
        CancellationToken cancellationToken)
    {
        long previousGeneration = Generation;
        int previousBuildHostCount = _folders.Length;
        int clearedCacheEntryCount = _diagnosticCache.Count;
        IReadOnlyList<TextDocumentItem> overlays = await CaptureOpenDocumentsAsync(cancellationToken)
            .ConfigureAwait(false);
        ImmutableArray<(string RootPath, Workspace Workspace, Solution Solution)> loadedFolders =
            await LoadFoldersAsync(roots, cancellationToken).ConfigureAwait(false);
        bool published = false;
        try
        {
            IReadOnlyList<TextDocumentItem> retainedOverlays =
            [
                .. overlays.Where(overlay =>
                    FindFolderIndex(overlay.Uri.GetFileSystemPath(), loadedFolders) >= 0)
            ];
            loadedFolders = ApplyOpenDocuments(loadedFolders, retainedOverlays);
            var documentVersions = new Dictionary<string, int>(
                retainedOverlays.Count,
                PathComparer);
            var razorDocuments = new Dictionary<string, SourceText>(
                retainedOverlays.Count,
                PathComparer);
            foreach (TextDocumentItem overlay in retainedOverlays)
            {
                string path = overlay.Uri.GetFileSystemPath();
                documentVersions.Add(path, overlay.Version);
                if (WorkspaceRazorDiagnosticService.IsRazorDocument(path))
                {
                    razorDocuments.Add(path, SourceText.From(overlay.Text, Encoding.UTF8));
                }
            }

            await PublishFoldersAsync(
                loadedFolders,
                documentVersions,
                razorDocuments,
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

        return new WorkspaceMaintenanceResult
        {
            PreviousGeneration = previousGeneration,
            CurrentGeneration = Generation,
            AffectedWorkspaceCount = roots.Count,
            RestoredEntryPointCount = restoredEntryPointCount,
            RestartedBuildHostCount = previousBuildHostCount,
            ClearedCacheEntryCount = clearedCacheEntryCount
        };
    }

    private async Task<IReadOnlyList<TextDocumentItem>> CaptureOpenDocumentsAsync(
        CancellationToken cancellationToken)
    {
        ImmutableArray<(string RootPath, Workspace Workspace, Solution Solution)> folders = _folders;
        ImmutableDictionary<string, SourceText> razorDocuments = _razorDocuments;
        var overlays = new List<TextDocumentItem>(_documentVersions.Count);
        foreach ((string path, int version) in _documentVersions.OrderBy(
            static pair => pair.Key,
            PathComparer))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (razorDocuments.TryGetValue(path, out SourceText? razorText))
            {
                overlays.Add(new TextDocumentItem
                {
                    Uri = DocumentUri.FromFileSystemPath(path),
                    LanguageId = "razor",
                    Version = version,
                    Text = razorText.ToString()
                });
                continue;
            }

            Document? document = null;
            foreach ((string _, Workspace _, Solution solution) in folders)
            {
                document = FindDocument(solution, path);
                if (document is not null)
                {
                    break;
                }
            }

            Document capturedDocument = document ?? throw new InvalidOperationException(
                $"The open document is absent from the workspace snapshot: {path}");
            SourceText text = await capturedDocument
                .GetTextAsync(cancellationToken)
                .ConfigureAwait(false);
            overlays.Add(new TextDocumentItem
            {
                Uri = DocumentUri.FromFileSystemPath(path),
                LanguageId = "csharp",
                Version = version,
                Text = text.ToString()
            });
        }

        return overlays;
    }

    private static ImmutableArray<(
        string RootPath,
        Workspace Workspace,
        Solution Solution)> ApplyOpenDocuments(
        ImmutableArray<(string RootPath, Workspace Workspace, Solution Solution)> folders,
        IReadOnlyList<TextDocumentItem> overlays)
    {
        ImmutableArray<(string RootPath, Workspace Workspace, Solution Solution)> current = folders;
        foreach (TextDocumentItem overlay in overlays)
        {
            string path = overlay.Uri.GetFileSystemPath();
            if (WorkspaceRazorDiagnosticService.IsRazorDocument(path))
            {
                continue;
            }

            int folderIndex = FindFolderIndex(path, current);
            if (folderIndex < 0)
            {
                throw new InvalidOperationException($"No workspace folder owns document {path}.");
            }

            (string rootPath, Workspace workspace, Solution solution) = current[folderIndex];
            Document? document = FindDocument(solution, path);
            if (document is null)
            {
                Project project = solution.Projects.FirstOrDefault()
                    ?? throw new InvalidOperationException("The workspace contains no C# project.");
                var documentId = DocumentId.CreateNewId(project.Id, debugName: path);
                solution = solution.AddDocument(
                    documentId,
                    Path.GetFileName(path),
                    SourceText.From(overlay.Text, Encoding.UTF8),
                    filePath: path);
            }
            else
            {
                solution = solution.WithDocumentText(
                    document.Id,
                    SourceText.From(overlay.Text, Encoding.UTF8),
                    PreservationMode.PreserveIdentity);
            }

            current = current.SetItem(folderIndex, (rootPath, workspace, solution));
        }

        return current;
    }

    private IReadOnlyList<string> GetRequiredWorkspaceRoots()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposeState) != 0, this);
        IReadOnlyList<string> roots = WorkspaceRoots;
        return roots.Count == 0
            ? throw new InvalidOperationException("The language server has no loaded workspace.")
            : roots;
    }
}
