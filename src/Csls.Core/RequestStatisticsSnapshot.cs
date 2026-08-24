namespace Csls.Core;

/// <summary>
/// Describes cumulative execution timings for one scheduled request name.
/// </summary>
public sealed class RequestStatisticsSnapshot
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
    /// Gets the mean execution duration.
    /// </summary>
    public TimeSpan AverageDuration { get; init; }

    /// <summary>
    /// Gets the longest execution duration.
    /// </summary>
    public TimeSpan MaxDuration { get; init; }
}
