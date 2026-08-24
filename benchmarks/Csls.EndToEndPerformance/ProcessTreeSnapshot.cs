namespace Csls.EndToEndPerformance;

/// <summary>
/// Describes one cross-platform launcher process-tree resource snapshot.
/// </summary>
internal sealed class ProcessTreeSnapshot
{
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
}
