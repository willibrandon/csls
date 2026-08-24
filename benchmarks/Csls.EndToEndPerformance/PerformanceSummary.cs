namespace Csls.EndToEndPerformance;

/// <summary>
/// Summarizes timing medians and resource maxima across fresh process iterations.
/// </summary>
internal sealed class PerformanceSummary
{
    /// <summary>
    /// Gets the median protocol startup time in milliseconds.
    /// </summary>
    public double MedianStartupMilliseconds { get; init; }

    /// <summary>
    /// Gets the median workspace-load time in milliseconds.
    /// </summary>
    public double MedianWorkspaceLoadMilliseconds { get; init; }

    /// <summary>
    /// Gets the median total ready time in milliseconds.
    /// </summary>
    public double MedianReadyMilliseconds { get; init; }

    /// <summary>
    /// Gets the largest ready-state process-tree count.
    /// </summary>
    public int MaximumProcessCount { get; init; }

    /// <summary>
    /// Gets the largest ready-state process-tree working set in bytes.
    /// </summary>
    public long MaximumWorkingSetBytes { get; init; }

    /// <summary>
    /// Gets the largest ready-state process-tree private memory in bytes.
    /// </summary>
    public long MaximumPrivateMemoryBytes { get; init; }

    /// <summary>
    /// Creates a summary from completed measurements.
    /// </summary>
    /// <param name="measurements">The completed iteration measurements.</param>
    /// <returns>The aggregate performance summary.</returns>
    internal static PerformanceSummary Create(IReadOnlyList<PerformanceMeasurement> measurements)
    {
        ArgumentNullException.ThrowIfNull(measurements);
        if (measurements.Count == 0)
        {
            throw new ArgumentException(
                "At least one measurement is required.",
                nameof(measurements));
        }

        return new PerformanceSummary
        {
            MedianStartupMilliseconds = Median(
                measurements.Select(static item => item.StartupMilliseconds)),
            MedianWorkspaceLoadMilliseconds = Median(
                measurements.Select(static item => item.WorkspaceLoadMilliseconds)),
            MedianReadyMilliseconds = Median(
                measurements.Select(static item => item.ReadyMilliseconds)),
            MaximumProcessCount = measurements.Max(static item => item.ProcessCount),
            MaximumWorkingSetBytes = measurements.Max(static item => item.WorkingSetBytes),
            MaximumPrivateMemoryBytes = measurements.Max(static item => item.PrivateMemoryBytes)
        };
    }

    private static double Median(IEnumerable<double> values)
    {
        double[] ordered = [.. values.Order()];
        int midpoint = ordered.Length / 2;
        return ordered.Length % 2 == 0
            ? (ordered[midpoint - 1] + ordered[midpoint]) / 2
            : ordered[midpoint];
    }
}
