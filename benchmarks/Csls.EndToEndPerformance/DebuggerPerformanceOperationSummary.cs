namespace Csls.EndToEndPerformance;

/// <summary>
/// Summarizes a single operation and request phase without mixing first and repeated requests.
/// </summary>
/// <param name="Name">The stable operation name.</param>
/// <param name="Phase">The independently aggregated request phase.</param>
/// <param name="SampleCount">The number of completed measurements.</param>
/// <param name="MedianMilliseconds">The median observed duration.</param>
/// <param name="MaximumMilliseconds">The largest observed duration.</param>
internal sealed record DebuggerPerformanceOperationSummary(string Name, string Phase, int SampleCount,
    double MedianMilliseconds, double MaximumMilliseconds);
