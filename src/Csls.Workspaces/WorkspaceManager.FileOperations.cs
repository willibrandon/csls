using Csls.Protocol;
using Microsoft.CodeAnalysis;
using System.Collections.Immutable;

namespace Csls.Workspaces;

public sealed partial class WorkspaceManager
{
    /// <summary>
    /// Reloads affected workspace topology after the client creates files or folders.
    /// </summary>
    /// <param name="parameters">The created workspace resources.</param>
    /// <param name="cancellationToken">The operation cancellation token.</param>
    /// <returns>The maintenance result, or null when no loaded root is affected.</returns>
    public Task<WorkspaceMaintenanceResult?> ApplyCreatedFilesAsync(
        CreateFilesParams parameters,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        ArgumentNullException.ThrowIfNull(parameters.Files);
        return ReloadAfterFileOperationsAsync(
            [.. parameters.Files.Select(static file => file.Uri.GetFileSystemPath())],
            renamedFiles: [],
            deletedFiles: [],
            cancellationToken);
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
