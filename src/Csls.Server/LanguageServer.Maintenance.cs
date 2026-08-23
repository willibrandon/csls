using Csls.Core;
using Csls.Workspaces;

namespace Csls.Server;

public sealed partial class LanguageServer
{
    /// <summary>
    /// Reloads every workspace through ordered mutation scheduling while preserving open overlays.
    /// </summary>
    /// <param name="cancellationToken">The operation cancellation token.</param>
    /// <returns>The completed workspace maintenance result.</returns>
    public Task<WorkspaceMaintenanceResult> ReloadWorkspaceAsync(
        CancellationToken cancellationToken) =>
        ScheduleMaintenanceAsync(
            "csls/workspace/reload",
            _workspaceManager.ReloadAsync,
            cancellationToken);

    /// <summary>
    /// Restores every workspace entry point and reloads the resulting Roslyn state.
    /// </summary>
    /// <param name="cancellationToken">The operation cancellation token.</param>
    /// <returns>The completed workspace maintenance result.</returns>
    public Task<WorkspaceMaintenanceResult> RestoreWorkspaceAsync(
        CancellationToken cancellationToken) =>
        ScheduleMaintenanceAsync(
            "csls/workspace/restore",
            _workspaceManager.RestoreAsync,
            cancellationToken);

    /// <summary>
    /// Recreates every Roslyn workspace host while preserving open document overlays.
    /// </summary>
    /// <param name="cancellationToken">The operation cancellation token.</param>
    /// <returns>The completed workspace maintenance result.</returns>
    public Task<WorkspaceMaintenanceResult> RestartBuildHostsAsync(
        CancellationToken cancellationToken) =>
        ScheduleMaintenanceAsync(
            "csls/buildHost/restart",
            _workspaceManager.RestartBuildHostsAsync,
            cancellationToken);

    /// <summary>
    /// Removes retained diagnostic and semantic-token results through ordered mutation scheduling.
    /// </summary>
    /// <param name="cancellationToken">The operation cancellation token.</param>
    /// <returns>The completed workspace maintenance result.</returns>
    public Task<WorkspaceMaintenanceResult> ClearCachesAsync(
        CancellationToken cancellationToken)
    {
        EnsureRunning();
        return _scheduler.ScheduleAsync(
            "csls/cache/clear",
            RequestMode.ReadWrite,
            () => _workspaceManager.Generation,
            context =>
            {
                context.CancellationToken.ThrowIfCancellationRequested();
                long generation = _workspaceManager.Generation;
                int clearedEntryCount = _workspaceManager.ClearCaches() +
                    _semanticTokensCache.Clear();
                return ValueTask.FromResult(new WorkspaceMaintenanceResult
                {
                    PreviousGeneration = generation,
                    CurrentGeneration = generation,
                    AffectedWorkspaceCount = _workspaceManager.WorkspaceRoots.Count,
                    ClearedCacheEntryCount = clearedEntryCount
                });
            },
            cancellationToken);
    }

    private Task<WorkspaceMaintenanceResult> ScheduleMaintenanceAsync(
        string name,
        Func<CancellationToken, Task<WorkspaceMaintenanceResult>> operation,
        CancellationToken cancellationToken)
    {
        EnsureRunning();
        return _scheduler.ScheduleAsync(
            name,
            RequestMode.ReadWrite,
            () => _workspaceManager.Generation,
            async context =>
            {
                int semanticTokenEntries = _semanticTokensCache.Clear();
                WorkspaceMaintenanceResult result = await operation(context.CancellationToken)
                    .ConfigureAwait(false);
                return new WorkspaceMaintenanceResult
                {
                    PreviousGeneration = result.PreviousGeneration,
                    CurrentGeneration = result.CurrentGeneration,
                    AffectedWorkspaceCount = result.AffectedWorkspaceCount,
                    RestoredEntryPointCount = result.RestoredEntryPointCount,
                    RestartedBuildHostCount = result.RestartedBuildHostCount,
                    ClearedCacheEntryCount = result.ClearedCacheEntryCount + semanticTokenEntries
                };
            },
            cancellationToken);
    }
}
