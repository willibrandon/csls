namespace Csls.Protocol;

/// <summary>
/// Describes cumulative execution timings for one request name.
/// </summary>
public sealed class CSharpDebugRequestStatisticsInfo
{
    /// <summary>
    /// Gets the protocol or control operation name.
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// Gets the number of completed executions.
    /// </summary>
    public long Count { get; init; }

    /// <summary>
    /// Gets the mean execution duration in milliseconds.
    /// </summary>
    public double AverageDurationMs { get; init; }

    /// <summary>
    /// Gets the longest execution duration in milliseconds.
    /// </summary>
    public double MaxDurationMs { get; init; }
}
