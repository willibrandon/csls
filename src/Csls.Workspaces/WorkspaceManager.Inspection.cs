using Microsoft.CodeAnalysis;
using System.Collections.Immutable;
using System.Globalization;
using RoslynDiagnostic = Microsoft.CodeAnalysis.Diagnostic;

namespace Csls.Workspaces;

public sealed partial class WorkspaceManager
{
    private const int MaximumInspectionDiagnostics = 500;

    /// <summary>
    /// Inspects only the current workspace generation and loaded folder summaries.
    /// </summary>
    /// <returns>The allocation-bounded workspace summary.</returns>
    public WorkspaceSummarySnapshot InspectSummary()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposeState) != 0, this);
        ImmutableArray<(string RootPath, Workspace Workspace, Solution Solution)> folders = _folders;
        return new WorkspaceSummarySnapshot
        {
            Generation = Generation,
            Workspaces =
            [
                .. folders.Select(static folder => new WorkspaceFolderInspection
                {
                    RootPath = folder.RootPath,
                    WorkspaceKind = folder.Workspace.GetType().Name,
                    ProjectCount = folder.Solution.ProjectIds.Count,
                    DocumentCount = folder.Solution.Projects.Sum(
                        static project => project.DocumentIds.Count)
                })
            ]
        };
    }

    /// <summary>
    /// Inspects the current immutable workspace generation using real Roslyn state.
    /// </summary>
    /// <param name="includeDiagnostics">Whether to evaluate compiler and analyzer diagnostics.</param>
    /// <param name="cancellationToken">The inspection cancellation token.</param>
    /// <returns>The bounded workspace, project, document, diagnostic, host, and cache state.</returns>
    public async Task<WorkspaceInspectionSnapshot> InspectAsync(
        bool includeDiagnostics,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposeState) != 0, this);
        long generation = Generation;
        ImmutableArray<(string RootPath, Workspace Workspace, Solution Solution)> folders = _folders;
        var workspaces = new List<WorkspaceFolderInspection>(folders.Length);
        var projects = new List<WorkspaceProjectInspection>();
        var documents = new List<WorkspaceDocumentInspection>();
        var diagnostics = new List<WorkspaceDiagnosticInspection>();
        var buildHosts = new List<WorkspaceBuildHostInspection>(folders.Length);
        int totalDiagnostics = 0;

        foreach ((string rootPath, Workspace workspace, Solution solution) in folders)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int documentCount = solution.Projects.Sum(static project => project.DocumentIds.Count);
            workspaces.Add(new WorkspaceFolderInspection
            {
                RootPath = rootPath,
                WorkspaceKind = workspace.GetType().Name,
                ProjectCount = solution.ProjectIds.Count,
                DocumentCount = documentCount
            });
            buildHosts.Add(new WorkspaceBuildHostInspection
            {
                ProcessId = Environment.ProcessId,
                Kind = workspace.GetType().Name,
                WorkspaceRoot = rootPath,
                ProjectCount = solution.ProjectIds.Count
            });

            foreach (Project project in solution.Projects.OrderBy(
                static project => project.Name,
                StringComparer.Ordinal))
            {
                cancellationToken.ThrowIfCancellationRequested();
                projects.Add(new WorkspaceProjectInspection
                {
                    Id = project.Id.Id.ToString("D", CultureInfo.InvariantCulture),
                    Name = project.Name,
                    FilePath = project.FilePath,
                    WorkspaceRoot = rootPath,
                    Language = project.Language,
                    DocumentCount = project.DocumentIds.Count,
                    AnalyzerReferenceCount = project.AnalyzerReferences.Count,
                    AnalyzerPaths =
                    [
                        .. project.AnalyzerReferences
                            .Select(static reference => reference.FullPath)
                            .Where(static path => path is not null)
                            .Cast<string>()
                            .Distinct(PathComparer)
                            .OrderBy(static path => path, PathComparer)
                    ]
                });
                foreach (Document document in project.Documents.OrderBy(
                    static document => document.FilePath ?? document.Name,
                    StringComparer.Ordinal))
                {
                    documents.Add(new WorkspaceDocumentInspection
                    {
                        Id = document.Id.Id.ToString("D", CultureInfo.InvariantCulture),
                        Name = document.Name,
                        FilePath = document.FilePath,
                        ProjectName = project.Name,
                        IsOpen = document.FilePath is not null &&
                            _documentVersions.ContainsKey(document.FilePath)
                    });
                }

                if (includeDiagnostics)
                {
                    ImmutableArray<RoslynDiagnostic> projectDiagnostics =
                        await _diagnosticCache.GetOrAddAsync(
                            generation,
                            project,
                            ComputeProjectDiagnosticsAsync,
                            cancellationToken).ConfigureAwait(false);
                    foreach (RoslynDiagnostic diagnostic in projectDiagnostics.OrderBy(
                        static diagnostic => diagnostic.Location.SourceSpan.Start))
                    {
                        if (diagnostic.Severity == DiagnosticSeverity.Hidden)
                        {
                            continue;
                        }

                        totalDiagnostics++;
                        if (diagnostics.Count == MaximumInspectionDiagnostics)
                        {
                            continue;
                        }

                        FileLinePositionSpan? lineSpan = diagnostic.Location.IsInSource
                            ? diagnostic.Location.GetLineSpan()
                            : null;
                        diagnostics.Add(new WorkspaceDiagnosticInspection
                        {
                            Id = diagnostic.Id,
                            Severity = diagnostic.Severity.ToString(),
                            Message = diagnostic.GetMessage(CultureInfo.InvariantCulture),
                            ProjectName = project.Name,
                            FilePath = lineSpan?.Path,
                            Line = lineSpan?.StartLinePosition.Line,
                            Character = lineSpan?.StartLinePosition.Character
                        });
                    }
                }
            }
        }

        return new WorkspaceInspectionSnapshot
        {
            Generation = generation,
            Workspaces = workspaces,
            Projects = projects,
            Documents = documents,
            Diagnostics = diagnostics,
            DiagnosticsLoaded = includeDiagnostics,
            TotalDiagnostics = totalDiagnostics,
            DiagnosticsTruncated = totalDiagnostics > diagnostics.Count,
            BuildHosts = buildHosts,
            DiagnosticCacheEntries = _diagnosticCache.Count
        };
    }
}
