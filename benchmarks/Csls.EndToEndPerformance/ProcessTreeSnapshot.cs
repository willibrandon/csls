namespace Csls.EndToEndPerformance;

/// <summary>
/// Describes one cross-platform launcher process-tree resource snapshot.
/// </summary>
internal sealed class ProcessTreeSnapshot
{
    /// <summary>
    /// Gets the sorted process identifiers in the launcher tree.
    /// </summary>
    internal required IReadOnlyList<int> ProcessIds { get; init; }

    /// <summary>
    /// Gets the number of processes in the launcher tree.
    /// </summary>
    internal int ProcessCount { get; init; }

    /// <summary>
    /// Gets the summed process-tree working set in bytes.
    /// </summary>
    internal long WorkingSetBytes { get; init; }

    /// <summary>
    /// Gets the summed process-tree private memory in bytes.
    /// </summary>
    internal long PrivateMemoryBytes { get; init; }

    /// <summary>
    /// Gets the summed process-tree processor time in ticks.
    /// </summary>
    internal long ProcessorTimeTicks { get; init; }
}
