using Csls.Core;
using Csls.Protocol;
using Csls.Workspaces;

namespace Csls.Server;

public sealed partial class LanguageServer
{
    /// <inheritdoc />
    public Task<CSharpDebugInfo> GetDebugInfoAsync(
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        WorkspaceSummarySnapshot workspace = _workspaceManager.InspectSummary();
        RequestSchedulerSnapshot requests = _scheduler.GetSnapshot();
        return Task.FromResult(new CSharpDebugInfo
        {
            Workspace = new CSharpDebugWorkspaceInfo
            {
                Phase = ((ServerWorkspacePhase)Volatile.Read(ref _workspacePhase)).ToString(),
                Generation = workspace.Generation,
                Folders =
                [
                    .. workspace.Workspaces.Select(static folder =>
                        new CSharpDebugWorkspaceFolderInfo
                        {
                            Uri = DocumentUri.FromFileSystemPath(folder.RootPath),
                            Name = GetWorkspaceFolderName(folder.RootPath),
                            WorkspaceKind = folder.WorkspaceKind,
                            ProjectCount = folder.ProjectCount,
                            DocumentCount = folder.DocumentCount
                        })
                ]
            },
            RequestQueue = new CSharpDebugRequestQueueInfo
            {
                Mode = requests.IsStopping ? "Stopping" : "Dispatching",
                Capacity = requests.Capacity,
                AcceptedRequests = requests.AcceptedRequests,
                CompletedRequests = requests.CompletedRequests,
                QueuedRequests = requests.QueuedRequests,
                RequestsTruncated = requests.ActiveRequestsTruncated,
                Requests =
                [
                    .. requests.ActiveRequests.Select(static request =>
                        new CSharpDebugRequestInfo
                        {
                            Ordinal = request.Ordinal,
                            CorrelationId = request.CorrelationId,
                            Name = request.Name,
                            Mode = request.Mode.ToString(),
                            Phase = request.Status.ToString(),
                            WorkspaceGeneration = request.WorkspaceGeneration,
                            DurationMs = request.Duration.TotalMilliseconds,
                            IsCancellationRequested = request.IsCancellationRequested
                        })
                ],
                Stats =
                [
                    .. requests.Statistics.Select(static statistic =>
                        new CSharpDebugRequestStatisticsInfo
                        {
                            Name = statistic.Name,
                            Count = statistic.Count,
                            AverageDurationMs = statistic.AverageDuration.TotalMilliseconds,
                            MaxDurationMs = statistic.MaxDuration.TotalMilliseconds
                        })
                ]
            }
        });
    }

    private static string GetWorkspaceFolderName(string rootPath)
    {
        string name = Path.GetFileName(Path.TrimEndingDirectorySeparator(rootPath));
        return string.IsNullOrEmpty(name) ? rootPath : name;
    }
}
