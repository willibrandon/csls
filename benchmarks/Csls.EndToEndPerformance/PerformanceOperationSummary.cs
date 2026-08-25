namespace Csls.EndToEndPerformance;

/// <summary>
/// Summarizes one operation across fresh process iterations.
/// </summary>
internal sealed class PerformanceOperationSummary
{
    /// <summary>
    /// Gets the stable operation name.
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// Gets the median elapsed time in milliseconds.
    /// </summary>
    public double MedianMilliseconds { get; init; }

    /// <summary>
    /// Gets the maximum elapsed time in milliseconds.
    /// </summary>
    public double MaximumMilliseconds { get; init; }
}
