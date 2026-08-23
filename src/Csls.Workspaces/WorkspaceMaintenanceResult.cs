namespace Csls.Workspaces;

/// <summary>
/// Describes the observable result of one ordered workspace maintenance operation.
/// </summary>
public sealed class WorkspaceMaintenanceResult
{
    /// <summary>
    /// Gets the workspace generation observed before the operation began.
    /// </summary>
    public long PreviousGeneration { get; init; }

    /// <summary>
    /// Gets the workspace generation published after the operation completed.
    /// </summary>
    public long CurrentGeneration { get; init; }

    /// <summary>
    /// Gets the number of workspace roots affected by the operation.
    /// </summary>
    public int AffectedWorkspaceCount { get; init; }

    /// <summary>
    /// Gets the number of solution or project entry points restored by the .NET CLI.
    /// </summary>
    public int RestoredEntryPointCount { get; init; }

    /// <summary>
    /// Gets the number of Roslyn workspace hosts recreated by the operation.
    /// </summary>
    public int RestartedBuildHostCount { get; init; }

    /// <summary>
    /// Gets the number of cache entries removed by the operation.
    /// </summary>
    public int ClearedCacheEntryCount { get; init; }
}
