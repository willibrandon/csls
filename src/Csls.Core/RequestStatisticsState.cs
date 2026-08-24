namespace Csls.Core;

/// <summary>
/// Accumulates consistent request execution timings for one operation name.
/// </summary>
internal sealed class RequestStatisticsState
{
    private readonly Lock _gate = new();
    private long _count;
    private long _totalTicks;
    private long _maxTicks;

    /// <summary>
    /// Records one completed request execution duration.
    /// </summary>
    /// <param name="duration">The monotonic execution duration.</param>
    internal void Record(TimeSpan duration)
    {
        long ticks = Math.Max(0, duration.Ticks);
        lock (_gate)
        {
            _count++;
            _totalTicks += ticks;
            _maxTicks = Math.Max(_maxTicks, ticks);
        }
    }

    /// <summary>
    /// Creates one consistent timing observation.
    /// </summary>
    /// <param name="name">The operation name represented by this accumulator.</param>
    /// <returns>The cumulative request timing observation.</returns>
    internal RequestStatisticsSnapshot GetSnapshot(string name)
    {
        lock (_gate)
        {
            return new RequestStatisticsSnapshot
            {
                Name = name,
                Count = _count,
                AverageDuration = _count == 0
                    ? TimeSpan.Zero
                    : TimeSpan.FromTicks(_totalTicks / _count),
                MaxDuration = TimeSpan.FromTicks(_maxTicks)
            };
        }
    }
}
