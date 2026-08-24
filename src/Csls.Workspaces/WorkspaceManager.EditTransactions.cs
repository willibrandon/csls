using Csls.Protocol;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using LspTextEdit = Csls.Protocol.TextEdit;

namespace Csls.Workspaces;

public sealed partial class WorkspaceManager
{
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

    private static void CommitStagedFiles(
        Dictionary<string, StagedWorkspaceFile> stagedFiles)
    {
        var committedFiles = new List<StagedWorkspaceFile>(stagedFiles.Count);
        try
        {
            foreach (StagedWorkspaceFile stagedFile in stagedFiles.Values
                .OrderBy(static file => file.IsNew)
                .ThenBy(static file => file.Path, PathComparer))
            {
                if (stagedFile.IsNew)
                {
                    File.Move(stagedFile.TempPath, stagedFile.Path);
                }
                else
                {
                    File.Replace(
                        stagedFile.TempPath,
                        stagedFile.Path,
                        stagedFile.BackupPath);
                }

                committedFiles.Add(stagedFile);
            }
        }
        catch (Exception commitException) when (
            commitException is IOException or UnauthorizedAccessException)
        {
            var rollbackExceptions = new List<Exception>();
            for (int index = committedFiles.Count - 1; index >= 0; index--)
            {
                StagedWorkspaceFile stagedFile = committedFiles[index];
                try
                {
                    if (stagedFile.IsNew)
                    {
                        File.Delete(stagedFile.Path);
                    }
                    else if (stagedFile.BackupPath is string backupPath &&
                        File.Exists(backupPath))
                    {
                        File.Move(backupPath, stagedFile.Path, overwrite: true);
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
                rollbackExceptions.Insert(0, commitException);
                throw new AggregateException(
                    "Applying the edit failed and at least one resource could not be restored.",
                    rollbackExceptions);
            }

            throw;
        }
    }

    private static void RollbackCommittedFiles(
        Dictionary<string, StagedWorkspaceFile> stagedFiles,
        Exception applicationException)
    {
        var rollbackExceptions = new List<Exception>();
        foreach (StagedWorkspaceFile stagedFile in stagedFiles.Values
            .OrderBy(static file => file.IsNew)
            .ThenBy(static file => file.Path, PathComparer)
            .Reverse())
        {
            try
            {
                if (stagedFile.IsNew)
                {
                    File.Delete(stagedFile.Path);
                }
                else if (stagedFile.BackupPath is string backupPath &&
                    File.Exists(backupPath))
                {
                    File.Move(backupPath, stagedFile.Path, overwrite: true);
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
            rollbackExceptions.Insert(0, applicationException);
            throw new AggregateException(
                "Applying the edit failed and at least one resource could not be restored.",
                rollbackExceptions);
        }
    }

    private void CleanupStagedBackups(
        Dictionary<string, StagedWorkspaceFile> stagedFiles)
    {
        foreach (StagedWorkspaceFile stagedFile in stagedFiles.Values)
        {
            if (stagedFile.BackupPath is not string backupPath)
            {
                continue;
            }

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
        Dictionary<string, StagedWorkspaceFile> stagedFiles)
    {
        for (int folderIndex = 0; folderIndex < _folders.Length; folderIndex++)
        {
            (string rootPath, Workspace workspace, Solution solution) = _folders[folderIndex];
            foreach (StagedWorkspaceFile stagedFile in stagedFiles.Values)
            {
                DocumentId[] documentIds =
                [
                    .. solution.Projects
                        .SelectMany(static project => project.Documents)
                        .Where(document => string.Equals(
                            document.FilePath,
                            stagedFile.Path,
                            PathComparison))
                        .Select(static document => document.Id)
                ];
                foreach (DocumentId documentId in documentIds)
                {
                    solution = solution.WithDocumentText(
                        documentId,
                        stagedFile.Text,
                        PreservationMode.PreserveIdentity);
                }

                solution = WithAdditionalDocumentText(
                    solution,
                    stagedFile.Path,
                    stagedFile.Text);
            }

            _folders = _folders.SetItem(folderIndex, (rootPath, workspace, solution));
        }
    }

    private void CleanupStagedFiles(
        Dictionary<string, StagedWorkspaceFile> stagedFiles)
    {
        foreach (StagedWorkspaceFile stagedFile in stagedFiles.Values)
        {
            try
            {
                File.Delete(stagedFile.TempPath);
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException)
            {
                LogEditArtifactCleanupFailure(stagedFile.TempPath, exception);
            }
        }
    }

    private void EnsureWorkspaceResourcePath(string path)
    {
        string fullPath = Path.GetFullPath(path);
        if (FindFolderIndex(fullPath, _folders) < 0)
        {
            throw new InvalidOperationException(
                $"Resource {fullPath} is outside the loaded workspace roots.");
        }
    }

    private async Task ReloadFoldersAfterResourceEditAsync(
        CancellationToken cancellationToken)
    {
        IReadOnlyList<TextDocumentItem> overlays = await CaptureOpenDocumentsAsync(
            cancellationToken).ConfigureAwait(false);
        IReadOnlyList<string> roots = WorkspaceRoots;
        ImmutableArray<(string RootPath, Workspace Workspace, Solution Solution)> loadedFolders =
            await LoadFoldersAsync(roots, cancellationToken).ConfigureAwait(false);
        bool published = false;
        try
        {
            loadedFolders = ApplyOpenDocuments(loadedFolders, overlays);
            ImmutableArray<(string RootPath, Workspace Workspace, Solution Solution)> previous =
                _folders;
            _folders = loadedFolders;
            _diagnosticCache.Clear();
            Interlocked.Increment(ref _generation);
            published = true;
            DisposeFolders(previous);
        }
        finally
        {
            if (!published)
            {
                DisposeFolders(loadedFolders);
            }
        }
    }
}
