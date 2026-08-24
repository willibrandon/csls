namespace Csls.Protocol;

/// <summary>
/// Describes one bounded observation of request scheduling and execution timings.
/// </summary>
public sealed class CSharpDebugRequestQueueInfo
{
    /// <summary>
    /// Gets the scheduler mode as Dispatching or Stopping.
    /// </summary>
    public required string Mode { get; init; }

    /// <summary>
    /// Gets the configured bounded queue capacity.
    /// </summary>
    public int Capacity { get; init; }

    /// <summary>
    /// Gets the number of requests accepted since session start.
    /// </summary>
    public long AcceptedRequests { get; init; }

    /// <summary>
    /// Gets the number of requests retired since session start.
    /// </summary>
    public long CompletedRequests { get; init; }

    /// <summary>
    /// Gets the number of requests waiting for scheduler admission.
    /// </summary>
    public int QueuedRequests { get; init; }

    /// <summary>
    /// Gets whether older active requests were omitted from this bounded observation.
    /// </summary>
    public bool RequestsTruncated { get; init; }

    /// <summary>
    /// Gets the retained queued and running requests in receive order.
    /// </summary>
    public required IReadOnlyList<CSharpDebugRequestInfo> Requests { get; init; }

    /// <summary>
    /// Gets cumulative execution timings grouped by request name.
    /// </summary>
    public required IReadOnlyList<CSharpDebugRequestStatisticsInfo> Stats { get; init; }
}
