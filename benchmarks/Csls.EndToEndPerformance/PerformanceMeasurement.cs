namespace Csls.EndToEndPerformance;

/// <summary>
/// Describes one fresh csls process and workspace measurement.
/// </summary>
internal sealed class PerformanceMeasurement
{
    /// <summary>
    /// Gets the one-based iteration number.
    /// </summary>
    public int Iteration { get; init; }

    /// <summary>
    /// Gets whether the iteration ran before or after the first workspace load.
    /// </summary>
    public required string CacheState { get; init; }

    /// <summary>
    /// Gets the time from process start through the first protocol response.
    /// </summary>
    public double StartupMilliseconds { get; init; }

    /// <summary>
    /// Gets the time from initialize response through a ready Roslyn workspace.
    /// </summary>
    public double WorkspaceLoadMilliseconds { get; init; }

    /// <summary>
    /// Gets the time from process start through a ready Roslyn workspace.
    /// </summary>
    public double ReadyMilliseconds { get; init; }

    /// <summary>
    /// Gets the loaded Roslyn project count.
    /// </summary>
    public int ProjectCount { get; init; }

    /// <summary>
    /// Gets the loaded Roslyn source-document count.
    /// </summary>
    public int DocumentCount { get; init; }

    /// <summary>
    /// Gets the ready-state launcher process-tree count.
    /// </summary>
    public int ProcessCount { get; init; }

    /// <summary>
    /// Gets the ready-state launcher process-tree working set in bytes.
    /// </summary>
    public long WorkingSetBytes { get; init; }

    /// <summary>
    /// Gets the ready-state launcher process-tree private memory in bytes.
    /// </summary>
    public long PrivateMemoryBytes { get; init; }
}
