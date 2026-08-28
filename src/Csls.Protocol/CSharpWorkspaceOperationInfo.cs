namespace Csls.Protocol;

/// <summary>
/// Describes one completed workspace maintenance operation requested by an editor.
/// </summary>
public sealed class CSharpWorkspaceOperationInfo
{
    /// <summary>
    /// Gets the stable operation name.
    /// </summary>
    public required string Operation { get; init; }

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
    /// Gets the number of restored solution or project entry points.
    /// </summary>
    public int RestoredEntryPointCount { get; init; }

    /// <summary>
    /// Gets the number of recreated Roslyn workspace hosts.
    /// </summary>
    public int RestartedBuildHostCount { get; init; }

    /// <summary>
    /// Gets the number of cleared workspace result cache entries.
    /// </summary>
    public int ClearedCacheEntryCount { get; init; }
}
