namespace Csls.Core;

/// <summary>
/// Describes one atomic observation of the bounded request scheduler.
/// </summary>
public sealed class RequestSchedulerSnapshot
{
    /// <summary>
    /// Gets the maximum number of retained request observations.
    /// </summary>
    public int ActivityCapacity { get; init; }

    /// <summary>
    /// Gets the configured bounded queue capacity.
    /// </summary>
    public int Capacity { get; init; }

    /// <summary>
    /// Gets the configured foreground concurrency limit.
    /// </summary>
    public int ForegroundConcurrency { get; init; }

    /// <summary>
    /// Gets the configured background concurrency limit.
    /// </summary>
    public int BackgroundConcurrency { get; init; }

    /// <summary>
    /// Gets the number of accepted requests since scheduler creation.
    /// </summary>
    public long AcceptedRequests { get; init; }

    /// <summary>
    /// Gets the number of retired requests since scheduler creation.
    /// </summary>
    public long CompletedRequests { get; init; }

    /// <summary>
    /// Gets the number of requests waiting for scheduler admission.
    /// </summary>
    public int QueuedRequests { get; init; }

    /// <summary>
    /// Gets the number of active foreground read requests.
    /// </summary>
    public int ActiveForegroundRequests { get; init; }

    /// <summary>
    /// Gets the number of active background read requests.
    /// </summary>
    public int ActiveBackgroundRequests { get; init; }

    /// <summary>
    /// Gets whether one workspace mutation is active.
    /// </summary>
    public bool IsMutationActive { get; init; }

    /// <summary>
    /// Gets whether the scheduler has begun stopping.
    /// </summary>
    public bool IsStopping { get; init; }

    /// <summary>
    /// Gets the total number of queued and running requests before result bounding.
    /// </summary>
    public int TotalActiveRequests { get; init; }

    /// <summary>
    /// Gets whether older active request observations were omitted.
    /// </summary>
    public bool ActiveRequestsTruncated { get; init; }

    /// <summary>
    /// Gets the retained queued and running request observations in receive order.
    /// </summary>
    public required IReadOnlyList<RequestActivitySnapshot> ActiveRequests { get; init; }
}
