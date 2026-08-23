namespace Csls.Control.Contracts;

/// <summary>
/// Describes one completed workspace operation returned through the control protocol.
/// </summary>
public sealed class ControlWorkspaceOperationResult
{
    /// <summary>
    /// Gets the stable name of the completed operation.
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
    /// Gets the number of solution or project entry points restored by the .NET CLI.
    /// </summary>
    public int RestoredEntryPointCount { get; init; }

    /// <summary>
    /// Gets the number of Roslyn workspace hosts recreated by the operation.
    /// </summary>
    public int RestartedBuildHostCount { get; init; }

    /// <summary>
    /// Gets the number of diagnostic, semantic-token, and pending-edit cache entries removed.
    /// </summary>
    public int ClearedCacheEntryCount { get; init; }
}
