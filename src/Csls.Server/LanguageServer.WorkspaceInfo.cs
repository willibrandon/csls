using Csls.Protocol;
using Csls.Workspaces;

namespace Csls.Server;

public sealed partial class LanguageServer
{
    /// <inheritdoc />
    public async Task<CSharpWorkspaceInfo> GetWorkspaceInfoAsync(
        CancellationToken cancellationToken)
    {
        WorkspaceInspectionSnapshot snapshot = await InspectWorkspaceAsync(
            includeDiagnostics: false,
            diagnosticsProjectId: null,
            cancellationToken).ConfigureAwait(false);
        return new CSharpWorkspaceInfo
        {
            Generation = snapshot.Generation,
            Workspaces =
            [
                .. snapshot.Workspaces.Select(static workspace =>
                    new CSharpWorkspaceFolderInfo
                    {
                        RootPath = workspace.RootPath,
                        WorkspaceKind = workspace.WorkspaceKind,
                        ProjectCount = workspace.ProjectCount,
                        DocumentCount = workspace.DocumentCount
                    })
            ],
            Projects =
            [
                .. snapshot.Projects.Select(static project =>
                    new CSharpWorkspaceProjectInfo
                    {
                        Id = project.Id,
                        Name = project.Name,
                        FilePath = project.FilePath,
                        WorkspaceRoot = project.WorkspaceRoot,
                        Language = project.Language,
                        DocumentCount = project.DocumentCount,
                        AnalyzerPaths = project.AnalyzerPaths,
                        ProjectReferenceIds = project.ProjectReferenceIds
                    })
            ],
            Documents =
            [
                .. snapshot.Documents.Select(static document =>
                    new CSharpWorkspaceDocumentInfo
                    {
                        Id = document.Id,
                        Name = document.Name,
                        FilePath = document.FilePath,
                        ProjectId = document.ProjectId,
                        IsOpen = document.IsOpen
                    })
            ]
        };
    }

    /// <inheritdoc />
    public async Task<CSharpWorkspaceOperationInfo> RestoreWorkspaceForClientAsync(
        CancellationToken cancellationToken)
    {
        WorkspaceMaintenanceResult result = await RestoreWorkspaceAsync(cancellationToken)
            .ConfigureAwait(false);
        return new CSharpWorkspaceOperationInfo
        {
            Operation = "restore",
            PreviousGeneration = result.PreviousGeneration,
            CurrentGeneration = result.CurrentGeneration,
            AffectedWorkspaceCount = result.AffectedWorkspaceCount,
            RestoredEntryPointCount = result.RestoredEntryPointCount,
            RestartedBuildHostCount = result.RestartedBuildHostCount,
            ClearedCacheEntryCount = result.ClearedCacheEntryCount
        };
    }
}
