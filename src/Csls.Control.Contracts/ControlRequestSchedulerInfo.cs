namespace Csls.Control.Contracts;

/// <summary>
/// Describes bounded request and queue activity exposed by the control protocol.
/// </summary>
public sealed class ControlRequestSchedulerInfo
{
    /// <summary>
    /// Gets the maximum number of retained active request observations.
    /// </summary>
    public int ActivityCapacity { get; init; }

    /// <summary>
    /// Gets the configured queue capacity.
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
    /// Gets the number of accepted requests since session start.
    /// </summary>
    public long AcceptedRequests { get; init; }

    /// <summary>
    /// Gets the number of retired requests since session start.
    /// </summary>
    public long CompletedRequests { get; init; }

    /// <summary>
    /// Gets the number of requests waiting for scheduler admission.
    /// </summary>
    public int QueuedRequests { get; init; }

    /// <summary>
    /// Gets the number of active foreground requests.
    /// </summary>
    public int ActiveForegroundRequests { get; init; }

    /// <summary>
    /// Gets the number of active background requests.
    /// </summary>
    public int ActiveBackgroundRequests { get; init; }

    /// <summary>
    /// Gets whether one workspace mutation is active.
    /// </summary>
    public bool IsMutationActive { get; init; }

    /// <summary>
    /// Gets whether the request scheduler is stopping.
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
    /// Gets the retained queued and running requests in receive order.
    /// </summary>
    public required IReadOnlyList<ControlRequestInfo> ActiveRequests { get; init; }

    /// <summary>
    /// Gets the active or most recently stopped bounded request trace.
    /// </summary>
    public required ControlTraceInfo Trace { get; init; }
}
