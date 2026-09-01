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
    /// <param name="cancellationToken">The inspection cancellation token.</param>
    /// <returns>The allocation-bounded workspace summary.</returns>
    public async Task<WorkspaceSummarySnapshot> InspectSummaryAsync(
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposeState) != 0, this);
        await _mutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ImmutableArray<(string RootPath, Workspace Workspace, Solution Solution)> folders =
                _folders;
            return new WorkspaceSummarySnapshot
            {
                Generation = Generation,
                Workspaces = CreateWorkspaceInspections(folders)
            };
        }
        finally
        {
            _mutationGate.Release();
        }
    }

    /// <summary>
    /// Inspects the current immutable workspace generation using real Roslyn state.
    /// </summary>
    /// <param name="includeDiagnostics">Whether to evaluate compiler and analyzer diagnostics.</param>
    /// <param name="diagnosticsProjectId">The optional project identifier used to bound diagnostic evaluation.</param>
    /// <param name="cancellationToken">The inspection cancellation token.</param>
    /// <returns>The bounded workspace, project, document, diagnostic, host, and cache state.</returns>
    public async Task<WorkspaceInspectionSnapshot> InspectAsync(
        bool includeDiagnostics,
        string? diagnosticsProjectId,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposeState) != 0, this);
        long generation = Generation;
        ImmutableArray<(string RootPath, Workspace Workspace, Solution Solution)> folders = _folders;
        IReadOnlyList<WorkspaceFolderInspection> workspaces = CreateWorkspaceInspections(folders);
        var projects = new List<WorkspaceProjectInspection>();
        var documents = new List<WorkspaceDocumentInspection>();
        var diagnostics = new List<WorkspaceDiagnosticInspection>();
        var diagnosticProjects = new List<Project>();
        int totalDiagnostics = 0;

        foreach ((string rootPath, Workspace workspace, Solution solution) in folders)
        {
            cancellationToken.ThrowIfCancellationRequested();
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
                    ],
                    ProjectReferenceIds =
                    [
                        .. project.ProjectReferences
                            .Select(static reference => reference.ProjectId.Id.ToString(
                                "D",
                                CultureInfo.InvariantCulture))
                            .OrderBy(static id => id, StringComparer.Ordinal)
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
                        ProjectId = project.Id.Id.ToString("D", CultureInfo.InvariantCulture),
                        IsOpen = document.FilePath is not null &&
                            _documentVersions.ContainsKey(document.FilePath)
                    });
                }

                if (includeDiagnostics &&
                    (diagnosticsProjectId is null || string.Equals(
                        project.Id.Id.ToString("D", CultureInfo.InvariantCulture),
                        diagnosticsProjectId,
                        StringComparison.OrdinalIgnoreCase)))
                {
                    diagnosticProjects.Add(project);
                }
            }
        }

        if (includeDiagnostics)
        {
            if (diagnosticsProjectId is not null && diagnosticProjects.Count == 0)
            {
                throw new KeyNotFoundException(
                    $"The diagnostics project was not found: {diagnosticsProjectId}");
            }

            ImmutableArray<RoslynDiagnostic>[] projectDiagnostics = await Task.WhenAll(
                diagnosticProjects.Select(async project =>
                {
                    VersionStamp projectVersion = await project
                        .GetDependentVersionAsync(cancellationToken)
                        .ConfigureAwait(false);
                    return await _diagnosticCache.GetOrAddAsync(
                        generation,
                        projectVersion,
                        project,
                        ComputeProjectDiagnosticsAsync,
                        cancellationToken).ConfigureAwait(false);
                })).ConfigureAwait(false);
            for (int projectIndex = 0;
                projectIndex < diagnosticProjects.Count;
                projectIndex++)
            {
                Project project = diagnosticProjects[projectIndex];
                foreach (RoslynDiagnostic diagnostic in projectDiagnostics[projectIndex].OrderBy(
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
            BuildHosts = folders.IsEmpty
                ? []
                :
                [
                    new WorkspaceBuildHostInspection
                    {
                        ProcessId = Environment.ProcessId,
                        Kind = string.Join(
                            "+",
                            folders
                                .Select(static folder => folder.Workspace.Kind)
                                .Distinct(StringComparer.Ordinal)
                                .Order(StringComparer.Ordinal)),
                        WorkspaceCount = folders.Length,
                        ProjectCount = folders.Sum(
                            static folder => folder.Solution.ProjectIds.Count)
                    }
                ],
            DiagnosticCacheEntries = _diagnosticCache.Count
        };
    }

    private static IReadOnlyList<WorkspaceFolderInspection> CreateWorkspaceInspections(
        ImmutableArray<(string RootPath, Workspace Workspace, Solution Solution)> folders) =>
        [
            .. folders
                .GroupBy(static folder => folder.RootPath, PathComparer)
                .OrderBy(static group => group.Key, PathComparer)
                .Select(static group => new WorkspaceFolderInspection
                {
                    RootPath = group.Key,
                    WorkspaceKind = string.Join(
                        "+",
                        group
                            .Select(static folder => folder.Workspace.Kind)
                            .Distinct(StringComparer.Ordinal)
                            .Order(StringComparer.Ordinal)),
                    ProjectCount = group.Sum(static folder => folder.Solution.ProjectIds.Count),
                    DocumentCount = group.Sum(GetDocumentCount)
                })
        ];

    private static int GetDocumentCount(
        (string RootPath, Workspace Workspace, Solution Solution) folder)
    {
        int documentCount = 0;
        foreach (Project project in folder.Solution.Projects)
        {
            documentCount += project.DocumentIds.Count;
        }

        return documentCount;
    }
}
