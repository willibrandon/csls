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
    /// Gets the per-operation timing summaries in execution order.
    /// </summary>
    public required IReadOnlyList<PerformanceOperationSummary> Operations { get; init; }

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
    /// Gets the largest measured process-tree processor time in milliseconds.
    /// </summary>
    public double MaximumProcessorTimeMilliseconds { get; init; }

    /// <summary>
    /// Gets the largest normalized process-tree processor use percentage.
    /// </summary>
    public double MaximumProcessorUtilizationPercent { get; init; }

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
            Operations = CreateOperationSummaries(measurements),
            MaximumProcessCount = measurements.Max(static item => item.ProcessCount),
            MaximumWorkingSetBytes = measurements.Max(static item => item.WorkingSetBytes),
            MaximumPrivateMemoryBytes = measurements.Max(static item => item.PrivateMemoryBytes),
            MaximumProcessorTimeMilliseconds = measurements.Max(
                static item => item.ProcessorTimeMilliseconds),
            MaximumProcessorUtilizationPercent = measurements.Max(
                static item => item.ProcessorUtilizationPercent)
        };
    }

    private static IReadOnlyList<PerformanceOperationSummary> CreateOperationSummaries(
        IReadOnlyList<PerformanceMeasurement> measurements)
    {
        string[] operationNames =
        [
            .. measurements[0].Operations.Select(static operation => operation.Name)
        ];
        bool operationOrderChanged = measurements.Any(measurement =>
            !operationNames.SequenceEqual(
                measurement.Operations.Select(static operation => operation.Name),
                StringComparer.Ordinal));
        if (operationOrderChanged)
        {
            throw new InvalidDataException(
                "Every performance iteration must execute the same ordered operations.");
        }

        return
        [
            .. operationNames.Select(name =>
            {
                double[] values =
                [
                    .. measurements.Select(measurement => measurement.Operations
                        .Single(operation => string.Equals(
                            operation.Name,
                            name,
                            StringComparison.Ordinal))
                        .Milliseconds)
                ];
                return new PerformanceOperationSummary
                {
                    Name = name,
                    MedianMilliseconds = Median(values),
                    MaximumMilliseconds = values.Max()
                };
            })
        ];
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
