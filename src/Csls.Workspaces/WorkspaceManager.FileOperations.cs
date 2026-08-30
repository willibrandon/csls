using Csls.Protocol;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using System.Collections.Immutable;
using System.Text;

namespace Csls.Workspaces;

public sealed partial class WorkspaceManager
{
    /// <summary>
    /// Reloads affected workspace topology after the client creates files or folders.
    /// </summary>
    /// <param name="parameters">The created workspace resources.</param>
    /// <param name="cancellationToken">The operation cancellation token.</param>
    /// <returns>The maintenance result, or null when no loaded root is affected.</returns>
    public async Task<WorkspaceMaintenanceResult?> ApplyCreatedFilesAsync(
        CreateFilesParams parameters,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        ArgumentNullException.ThrowIfNull(parameters.Files);
        string[] paths =
        [
            .. parameters.Files.Select(static file => file.Uri.GetFileSystemPath())
        ];
        (bool handled, WorkspaceMaintenanceResult? result) =
            await TryApplyCreatedSourceDocumentsAsync(paths, cancellationToken)
                .ConfigureAwait(false);
        if (handled)
        {
            return result;
        }

        return await ReloadAfterFileOperationsAsync(
            paths,
            renamedFiles: [],
            deletedFiles: [],
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Reloads affected workspace topology and remaps open overlays after client renames.
    /// </summary>
    /// <param name="parameters">The renamed workspace resources.</param>
    /// <param name="cancellationToken">The operation cancellation token.</param>
    /// <returns>The maintenance result, or null when no loaded root is affected.</returns>
    public Task<WorkspaceMaintenanceResult?> ApplyRenamedFilesAsync(
        RenameFilesParams parameters,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        ArgumentNullException.ThrowIfNull(parameters.Files);
        string[] affectedPaths =
        [
            .. parameters.Files.SelectMany(static file => new[]
            {
                file.OldUri.GetFileSystemPath(),
                file.NewUri.GetFileSystemPath()
            })
        ];
        return ReloadAfterFileOperationsAsync(
            affectedPaths,
            parameters.Files,
            deletedFiles: [],
            cancellationToken);
    }

    /// <summary>
    /// Reloads affected workspace topology and removes overlays after client deletions.
    /// </summary>
    /// <param name="parameters">The deleted workspace resources.</param>
    /// <param name="cancellationToken">The operation cancellation token.</param>
    /// <returns>The maintenance result, or null when no loaded root is affected.</returns>
    public Task<WorkspaceMaintenanceResult?> ApplyDeletedFilesAsync(
        DeleteFilesParams parameters,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        ArgumentNullException.ThrowIfNull(parameters.Files);
        return ReloadAfterFileOperationsAsync(
            [.. parameters.Files.Select(static file => file.Uri.GetFileSystemPath())],
            renamedFiles: [],
            parameters.Files,
            cancellationToken);
    }

    /// <summary>
    /// Reloads workspace state after files changed outside client-owned document overlays.
    /// </summary>
    /// <param name="parameters">The changed workspace files.</param>
    /// <param name="cancellationToken">The operation cancellation token.</param>
    /// <returns>The maintenance result, or null when no loaded root is affected.</returns>
    public async Task<WorkspaceMaintenanceResult?> ApplyChangedFilesAsync(
        DidChangeWatchedFilesParams parameters,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        ArgumentNullException.ThrowIfNull(parameters.Changes);
        FileEvent[] changes = [.. parameters.Changes];
        FileEvent? unsupportedChange = changes.FirstOrDefault(static change =>
            change.Type is not (
                FileChangeType.Created or
                FileChangeType.Changed or
                FileChangeType.Deleted));
        if (unsupportedChange is not null)
        {
            throw new InvalidDataException(
                $"Unsupported watched file change type {(int)unsupportedChange.Type}.");
        }

        changes =
        [
            .. changes.Where(change => !IsStaleFileBasedAppProjectEvent(change))
                .GroupBy(
                    static change => change.Uri.GetFileSystemPath(),
                    PathComparer)
                .Select(group => new FileEvent
                {
                    Uri = group.First().Uri,
                    Type = GetEffectiveFileChangeType(group.Key)
                })
        ];
        if (changes.Length == 0)
        {
            return null;
        }

        string[] paths =
        [.. changes.Select(static change => change.Uri.GetFileSystemPath())];
        if (changes.All(static change => change.Type is FileChangeType.Created))
        {
            (bool handled, WorkspaceMaintenanceResult? result) =
                await TryApplyCreatedSourceDocumentsAsync(paths, cancellationToken)
                    .ConfigureAwait(false);
            if (handled)
            {
                return result;
            }
        }

        bool requiresReload = changes.Any(static change =>
            change.Type is not FileChangeType.Changed ||
            RequiresWorkspaceReload(change.Uri.GetFileSystemPath()));
        if (requiresReload)
        {
            return await ReloadAfterFileOperationsAsync(
                paths,
                renamedFiles: [],
                deletedFiles: [],
                cancellationToken).ConfigureAwait(false);
        }

        return await ApplyChangedDocumentTextsAsync(paths, cancellationToken)
            .ConfigureAwait(false);
    }

    private FileChangeType GetEffectiveFileChangeType(string path)
    {
        if (!File.Exists(path))
        {
            return FileChangeType.Deleted;
        }

        return _folders.Any(folder =>
            !folder.Solution.GetDocumentIdsWithFilePath(path).IsDefaultOrEmpty)
            ? FileChangeType.Changed
            : FileChangeType.Created;
    }

    /// <summary>
    /// Gets a stable ordered copy of document URIs currently owned by the client.
    /// </summary>
    /// <returns>The open document URIs.</returns>
    public IReadOnlyList<DocumentUri> GetOpenDocumentUris() =>
        [
            .. _documentVersions.Keys
                .Order(PathComparer)
                .Select(DocumentUri.FromFileSystemPath)
        ];

    private async Task<WorkspaceMaintenanceResult?> ReloadAfterFileOperationsAsync(
        string[] affectedPaths,
        IReadOnlyList<FileRename> renamedFiles,
        IReadOnlyList<FileDelete> deletedFiles,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ImmutableArray<(string RootPath, Workspace Workspace, Solution Solution)> folders =
            _folders;
        bool affectsLoadedSource = false;
        foreach (string path in affectedPaths)
        {
            int folderIndex = FindFolderIndex(path, folders);
            if (folderIndex >= 0 &&
                !WorkspaceDiscovery.IsExcludedPath(folders[folderIndex].RootPath, path))
            {
                affectsLoadedSource = true;
                break;
            }
        }

        if (!affectsLoadedSource)
        {
            return null;
        }

        return await ReloadAsync(
            GetRequiredWorkspaceRoots(),
            restoredEntryPointCount: 0,
            renamedFiles,
            deletedFiles,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<WorkspaceMaintenanceResult?> ApplyChangedDocumentTextsAsync(
        string[] paths,
        CancellationToken cancellationToken)
    {
        bool requiresReload = false;
        WorkspaceMaintenanceResult? result = null;
        await _mutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ImmutableArray<(string RootPath, Workspace Workspace, Solution Solution)> folders =
                _folders;
            var changedFolderIndexes = new HashSet<int>();
            int clearedCacheEntryCount = _diagnosticCache.Count;
            for (int pathIndex = 0; pathIndex < paths.Length; pathIndex++)
            {
                string path = paths[pathIndex];
                int folderIndex = FindFolderIndex(path, folders);
                if (folderIndex < 0 ||
                    WorkspaceDiscovery.IsExcludedPath(folders[folderIndex].RootPath, path) ||
                    _documentVersions.ContainsKey(path))
                {
                    continue;
                }

                if (!File.Exists(path))
                {
                    requiresReload = true;
                    break;
                }

                (string rootPath, Workspace workspace, Solution solution) = folders[folderIndex];
                ImmutableArray<DocumentId> documentIds = solution.GetDocumentIdsWithFilePath(path);
                if (documentIds.IsDefaultOrEmpty)
                {
                    requiresReload = true;
                    break;
                }

                string persistedText = await File.ReadAllTextAsync(path, cancellationToken)
                    .ConfigureAwait(false);
                var text = SourceText.From(persistedText, Encoding.UTF8);
                bool changed = false;
                for (int documentIndex = 0; documentIndex < documentIds.Length; documentIndex++)
                {
                    DocumentId documentId = documentIds[documentIndex];
                    if (solution.GetDocument(documentId) is not null)
                    {
                        solution = solution.WithDocumentText(
                            documentId,
                            text,
                            PreservationMode.PreserveIdentity);
                        changed = true;
                    }
                    else if (solution.GetAdditionalDocument(documentId) is not null)
                    {
                        solution = solution.WithAdditionalDocumentText(
                            documentId,
                            text,
                            PreservationMode.PreserveIdentity);
                        changed = true;
                    }
                }

                if (!changed)
                {
                    requiresReload = true;
                    break;
                }

                folders = folders.SetItem(folderIndex, (rootPath, workspace, solution));
                changedFolderIndexes.Add(folderIndex);
            }

            if (!requiresReload && changedFolderIndexes.Count > 0)
            {
                long previousGeneration = Generation;
                _folders = folders;
                long currentGeneration = Interlocked.Increment(ref _generation);
                result = new WorkspaceMaintenanceResult
                {
                    PreviousGeneration = previousGeneration,
                    CurrentGeneration = currentGeneration,
                    AffectedWorkspaceCount = changedFolderIndexes.Count,
                    ClearedCacheEntryCount = clearedCacheEntryCount
                };
            }
        }
        finally
        {
            _mutationGate.Release();
        }

        if (requiresReload)
        {
            return await ReloadAfterFileOperationsAsync(
                paths,
                renamedFiles: [],
                deletedFiles: [],
                cancellationToken).ConfigureAwait(false);
        }

        return result;
    }

    private async Task<(bool Handled, WorkspaceMaintenanceResult? Result)>
        TryApplyCreatedSourceDocumentsAsync(
            string[] paths,
            CancellationToken cancellationToken)
    {
        if (paths.Any(static path =>
                !File.Exists(path) ||
                !Path.GetExtension(path).Equals(".cs", StringComparison.OrdinalIgnoreCase)))
        {
            return (false, null);
        }

        await _mutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ImmutableArray<(string RootPath, Workspace Workspace, Solution Solution)> folders =
                _folders;
            var changedFolderIndexes = new HashSet<int>();
            int clearedCacheEntryCount = _diagnosticCache.Count;
            for (int pathIndex = 0; pathIndex < paths.Length; pathIndex++)
            {
                string path = paths[pathIndex];
                int folderIndex = FindFolderIndex(path, folders);
                if (folderIndex < 0 ||
                    WorkspaceDiscovery.IsExcludedPath(folders[folderIndex].RootPath, path))
                {
                    continue;
                }

                (string rootPath, Workspace workspace, Solution solution) = folders[folderIndex];
                if (!solution.GetDocumentIdsWithFilePath(path).IsDefaultOrEmpty)
                {
                    continue;
                }

                Project[] projects = FindProjectsForCreatedSourceDocument(solution, path);
                if (projects.Length == 0)
                {
                    return (false, null);
                }

                string persistedText = await File.ReadAllTextAsync(path, cancellationToken)
                    .ConfigureAwait(false);
                var text = SourceText.From(persistedText, Encoding.UTF8);
                for (int projectIndex = 0; projectIndex < projects.Length; projectIndex++)
                {
                    Project project = projects[projectIndex];
                    var documentId = DocumentId.CreateNewId(project.Id, debugName: path);
                    solution = solution.AddDocument(
                        documentId,
                        Path.GetFileName(path),
                        text,
                        filePath: path);
                }

                folders = folders.SetItem(folderIndex, (rootPath, workspace, solution));
                changedFolderIndexes.Add(folderIndex);
            }

            if (changedFolderIndexes.Count == 0)
            {
                return (true, null);
            }

            long previousGeneration = Generation;
            _folders = folders;
            _diagnosticCache.Clear();
            long currentGeneration = Interlocked.Increment(ref _generation);
            return (
                true,
                new WorkspaceMaintenanceResult
                {
                    PreviousGeneration = previousGeneration,
                    CurrentGeneration = currentGeneration,
                    AffectedWorkspaceCount = changedFolderIndexes.Count,
                    ClearedCacheEntryCount = clearedCacheEntryCount
                });
        }
        finally
        {
            _mutationGate.Release();
        }
    }

    private static Project[] FindProjectsForCreatedSourceDocument(
        Solution solution,
        string path)
    {
        (Project Project, string DirectoryPath)[] candidates =
        [
            .. solution.Projects
                .Where(static project =>
                    string.Equals(project.Language, LanguageNames.CSharp, StringComparison.Ordinal) &&
                    !string.IsNullOrWhiteSpace(project.FilePath))
                .Select(static project => (
                    Project: project,
                    DirectoryPath: Path.GetDirectoryName(project.FilePath!)!))
                .Where(candidate => IsPathAtOrBelow(path, candidate.DirectoryPath))
        ];
        if (candidates.Length == 0)
        {
            return [];
        }

        int nearestDirectoryLength = candidates.Max(static candidate =>
            candidate.DirectoryPath.Length);
        return
        [
            .. candidates
                .Where(candidate => candidate.DirectoryPath.Length == nearestDirectoryLength)
                .Select(static candidate => candidate.Project)
        ];
    }

    private static bool RequiresWorkspaceReload(string path)
    {
        string extension = Path.GetExtension(path);
        return !extension.Equals(".cs", StringComparison.OrdinalIgnoreCase) &&
            !extension.Equals(".csx", StringComparison.OrdinalIgnoreCase) &&
            !extension.Equals(".cshtml", StringComparison.OrdinalIgnoreCase) &&
            !extension.Equals(".razor", StringComparison.OrdinalIgnoreCase);
    }

    private bool IsStaleFileBasedAppProjectEvent(FileEvent change)
    {
        string projectPath = change.Uri.GetFileSystemPath();
        const string projectSuffix = ".csproj";
        if (File.Exists(projectPath) ||
            !projectPath.EndsWith(projectSuffix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        string entryPointPath = projectPath[..^projectSuffix.Length];
        return _folders.Any(folder => folder.Solution.Projects.Any(project =>
            project.FilePath is not null &&
            PathComparer.Equals(project.FilePath, entryPointPath) &&
            WorkspaceDiscovery.IsFileBasedApp(entryPointPath)));
    }

    private static IReadOnlyList<TextDocumentItem> TransformFileOperationOverlays(
        IReadOnlyList<TextDocumentItem> overlays,
        IReadOnlyList<FileRename> renamedFiles,
        IReadOnlyList<FileDelete> deletedFiles)
    {
        (string OldPath, string NewPath)[] renames =
        [
            .. renamedFiles
                .Select(static file => (
                    OldPath: file.OldUri.GetFileSystemPath(),
                    NewPath: file.NewUri.GetFileSystemPath()))
                .OrderByDescending(static rename => rename.OldPath.Length)
        ];
        string[] deletedPaths =
        [
            .. deletedFiles.Select(static file => file.Uri.GetFileSystemPath())
        ];
        var transformed = new Dictionary<string, TextDocumentItem>(PathComparer);
        foreach (TextDocumentItem overlay in overlays)
        {
            string path = overlay.Uri.GetFileSystemPath();
            if (deletedPaths.Any(deletedPath => IsPathAtOrBelow(path, deletedPath)))
            {
                continue;
            }

            string transformedPath = path;
            foreach ((string oldPath, string newPath) in renames)
            {
                if (TryMapRenamedPath(path, oldPath, newPath, out string mappedPath))
                {
                    transformedPath = mappedPath;
                    break;
                }
            }

            TextDocumentItem transformedOverlay = string.Equals(
                path,
                transformedPath,
                StringComparison.Ordinal)
                ? overlay
                : overlay with { Uri = DocumentUri.FromFileSystemPath(transformedPath) };
            if (!transformed.TryAdd(transformedPath, transformedOverlay))
            {
                throw new InvalidOperationException(
                    $"File operations map multiple open documents to {transformedPath}.");
            }
        }

        return
        [
            .. transformed
                .OrderBy(static pair => pair.Key, PathComparer)
                .Select(static pair => pair.Value)
        ];
    }

    private static bool TryMapRenamedPath(
        string path,
        string oldPath,
        string newPath,
        out string mappedPath)
    {
        string relativePath = Path.GetRelativePath(oldPath, path);
        if (string.Equals(relativePath, ".", StringComparison.Ordinal))
        {
            mappedPath = newPath;
            return true;
        }

        if (Path.IsPathRooted(relativePath) ||
            relativePath.Equals("..", StringComparison.Ordinal) ||
            relativePath.StartsWith(
                ".." + Path.DirectorySeparatorChar,
                StringComparison.Ordinal))
        {
            mappedPath = path;
            return false;
        }

        mappedPath = Path.GetFullPath(Path.Join(newPath, relativePath));
        return true;
    }

    private static bool IsPathAtOrBelow(string path, string parentPath) =>
        TryMapRenamedPath(path, parentPath, parentPath, out _);
}
