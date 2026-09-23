namespace Csls.EndToEndPerformance;

/// <summary>
/// Records debugger, evaluator, and target resources at a defined stopped-state checkpoint.
/// </summary>
/// <param name="Phase">The checkpoint at which the processes were observed.</param>
/// <param name="ProcessIds">The actual debugger process tree, including the target.</param>
/// <param name="WorkingSetBytes">The summed resident working set.</param>
/// <param name="PrivateMemoryBytes">The summed private memory on Windows and Linux.</param>
/// <param name="ProcessorTimeMilliseconds">The cumulative processor time of the observed tree.</param>
internal sealed record DebuggerPerformanceResources(string Phase, IReadOnlyList<int> ProcessIds,
    long WorkingSetBytes, long? PrivateMemoryBytes, double ProcessorTimeMilliseconds);
