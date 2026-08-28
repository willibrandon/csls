using Csls.Protocol;
using Microsoft.CodeAnalysis;
using System.Collections.Immutable;

namespace Csls.Workspaces;

public sealed partial class WorkspaceManager
{
    private const int MaximumWorkspaceDiagnosticDocuments = 10_000;

    /// <summary>
    /// Gets complete or unchanged diagnostics for every document in one immutable workspace snapshot.
    /// </summary>
    /// <param name="parameters">The provider identifier and client-retained result identifiers.</param>
    /// <param name="reportInformationAsHint">Whether information is presented as a hint.</param>
    /// <param name="cancellationToken">The operation cancellation token.</param>
    /// <returns>The ordered workspace diagnostic report.</returns>
    public async Task<WorkspaceDiagnosticReport> GetWorkspaceDiagnosticsAsync(
        WorkspaceDiagnosticParams parameters,
        bool reportInformationAsHint,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        if (parameters.PreviousResultIds.Count > MaximumWorkspaceDiagnosticDocuments)
        {
            throw new InvalidDataException(
                $"A workspace diagnostic request cannot contain more than " +
                $"{MaximumWorkspaceDiagnosticDocuments} previous results.");
        }

        var previousResults = new Dictionary<string, string>(PathComparer);
        foreach (PreviousResultId previousResult in parameters.PreviousResultIds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string path = previousResult.Uri.GetFileSystemPath();
            if (!previousResults.TryAdd(path, previousResult.Value))
            {
                throw new InvalidDataException(
                    $"The workspace diagnostic request contains duplicate results for {path}.");
            }
        }

        string[] documentPaths = GetWorkspaceDiagnosticDocumentPaths();
        var items = new List<WorkspaceDocumentDiagnosticReport>(documentPaths.Length);
        foreach (string path in documentPaths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            previousResults.TryGetValue(path, out string? previousResultId);
            var uri = DocumentUri.FromFileSystemPath(path);
            DocumentDiagnosticReport report = await GetDiagnosticsAsync(
                new DocumentDiagnosticParams
                {
                    TextDocument = new TextDocumentIdentifier
                    {
                        Uri = uri
                    },
                    Identifier = parameters.Identifier,
                    PreviousResultId = previousResultId
                },
                reportInformationAsHint,
                cancellationToken).ConfigureAwait(false);
            items.Add(new WorkspaceDocumentDiagnosticReport
            {
                Kind = report.Kind,
                Uri = uri,
                Version = _documentVersions.TryGetValue(path, out int version)
                    ? version
                    : null,
                ResultId = report.ResultId,
                Items = report.Items
            });
        }

        return new WorkspaceDiagnosticReport { Items = items };
    }

    private string[] GetWorkspaceDiagnosticDocumentPaths()
    {
        ImmutableArray<(string RootPath, Workspace Workspace, Solution Solution)> folders = _folders;
        var paths = new HashSet<string>(PathComparer);
        foreach ((string rootPath, Workspace _, Solution solution) in folders)
        {
            foreach (Project project in solution.Projects)
            {
                foreach (Document document in project.Documents.Where(document =>
                    document.FilePath is string path &&
                    !WorkspaceDiscovery.IsExcludedPath(rootPath, path)))
                {
                    AddWorkspaceDiagnosticDocumentPath(paths, document.FilePath!);
                }

                foreach (TextDocument document in project.AdditionalDocuments.Where(document =>
                    document.FilePath is string path &&
                    !WorkspaceDiscovery.IsExcludedPath(rootPath, path) &&
                    WorkspaceRazorDiagnosticService.IsRazorDocument(path)))
                {
                    AddWorkspaceDiagnosticDocumentPath(paths, document.FilePath!);
                }
            }
        }

        return [.. paths.Order(PathComparer)];
    }

    private static void AddWorkspaceDiagnosticDocumentPath(
        HashSet<string> paths,
        string path)
    {
        paths.Add(Path.GetFullPath(path));
        if (paths.Count > MaximumWorkspaceDiagnosticDocuments)
        {
            throw new InvalidOperationException(
                $"The workspace exceeds the limit of " +
                $"{MaximumWorkspaceDiagnosticDocuments} diagnostic documents.");
        }
    }
}
