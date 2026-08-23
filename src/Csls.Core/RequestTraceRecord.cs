namespace Csls.Core;

/// <summary>
/// Maintains the mutable bounded trace state for one scheduled request.
/// </summary>
internal sealed class RequestTraceRecord
{
    private const int MaximumExceptionMessageLength = 2048;
    private readonly Lock _gate = new();
    private readonly TimeProvider _timeProvider;
    private readonly long _acceptedTimestamp;
    private long? _startedTimestamp;
    private long? _completedTimestamp;
    private long? _workspaceGeneration;
    private DateTimeOffset? _startedAt;
    private DateTimeOffset? _completedAt;
    private bool _isCancellationRequested;
    private RequestExecutionStatus _status;
    private string? _exceptionType;
    private string? _exceptionMessage;

    /// <summary>
    /// Initializes one trace record from the current request state.
    /// </summary>
    /// <param name="traceId">The owning trace identifier.</param>
    /// <param name="activity">The request activity captured at enrollment.</param>
    /// <param name="timeProvider">The scheduler time provider.</param>
    /// <param name="acceptedTimestamp">The monotonic receive timestamp.</param>
    /// <param name="startedTimestamp">The optional monotonic execution start timestamp.</param>
    internal RequestTraceRecord(
        Guid traceId,
        RequestActivitySnapshot activity,
        TimeProvider timeProvider,
        long acceptedTimestamp,
        long? startedTimestamp)
    {
        TraceId = traceId;
        Ordinal = activity.Ordinal;
        CorrelationId = activity.CorrelationId;
        Name = activity.Name;
        Mode = activity.Mode;
        AcceptedAt = activity.AcceptedAt;
        _workspaceGeneration = activity.WorkspaceGeneration;
        _startedAt = activity.StartedAt;
        _isCancellationRequested = activity.IsCancellationRequested;
        _status = activity.Status;
        _timeProvider = timeProvider;
        _acceptedTimestamp = acceptedTimestamp;
        _startedTimestamp = startedTimestamp;
    }

    /// <summary>
    /// Gets the trace session that owns this record.
    /// </summary>
    internal Guid TraceId { get; }

    /// <summary>
    /// Gets the request receive ordinal.
    /// </summary>
    internal long Ordinal { get; }

    /// <summary>
    /// Gets the stable request correlation identifier.
    /// </summary>
    internal Guid CorrelationId { get; }

    /// <summary>
    /// Gets the protocol or control operation name.
    /// </summary>
    internal string Name { get; }

    /// <summary>
    /// Gets the scheduler concurrency mode.
    /// </summary>
    internal RequestMode Mode { get; }

    /// <summary>
    /// Gets the wall-clock request receive time.
    /// </summary>
    internal DateTimeOffset AcceptedAt { get; }

    /// <summary>
    /// Marks the request as executing against one workspace generation.
    /// </summary>
    /// <param name="workspaceGeneration">The captured workspace generation.</param>
    /// <param name="startedAt">The wall-clock execution start time.</param>
    /// <param name="startedTimestamp">The monotonic execution start timestamp.</param>
    internal void MarkStarted(
        long workspaceGeneration,
        DateTimeOffset startedAt,
        long startedTimestamp)
    {
        lock (_gate)
        {
            _workspaceGeneration = workspaceGeneration;
            _startedAt = startedAt;
            _startedTimestamp = startedTimestamp;
            _status = RequestExecutionStatus.Running;
        }
    }

    /// <summary>
    /// Records that peer or server cancellation was requested.
    /// </summary>
    internal void MarkCancellationRequested()
    {
        lock (_gate)
        {
            _isCancellationRequested = true;
        }
    }

    /// <summary>
    /// Records the terminal request state and optional failure details.
    /// </summary>
    /// <param name="status">The terminal lifecycle status.</param>
    /// <param name="exception">The exception that ended a failed request.</param>
    /// <param name="completedAt">The wall-clock completion time.</param>
    /// <param name="completedTimestamp">The monotonic completion timestamp.</param>
    internal void Complete(
        RequestExecutionStatus status,
        Exception? exception,
        DateTimeOffset completedAt,
        long completedTimestamp)
    {
        lock (_gate)
        {
            _status = status;
            _completedAt = completedAt;
            _completedTimestamp = completedTimestamp;
            _isCancellationRequested |= status == RequestExecutionStatus.Canceled;
            _exceptionType = exception?.GetType().Name;
            _exceptionMessage = BoundExceptionMessage(exception?.Message);
        }
    }

    /// <summary>
    /// Creates one immutable trace entry from the current lifecycle state.
    /// </summary>
    /// <returns>The request lifecycle entry.</returns>
    internal RequestTraceEntry GetSnapshot()
    {
        lock (_gate)
        {
            long endTimestamp = _completedTimestamp ?? _timeProvider.GetTimestamp();
            long startTimestamp = _startedTimestamp ?? _acceptedTimestamp;
            return new RequestTraceEntry
            {
                Ordinal = Ordinal,
                CorrelationId = CorrelationId,
                Name = Name,
                Mode = Mode,
                WorkspaceGeneration = _workspaceGeneration,
                AcceptedAt = AcceptedAt,
                StartedAt = _startedAt,
                CompletedAt = _completedAt,
                Duration = _timeProvider.GetElapsedTime(startTimestamp, endTimestamp),
                IsCancellationRequested = _isCancellationRequested,
                Status = _status,
                ExceptionType = _exceptionType,
                ExceptionMessage = _exceptionMessage
            };
        }
    }

    private static string? BoundExceptionMessage(string? message) =>
        message is null || message.Length <= MaximumExceptionMessageLength
            ? message
            : message[..MaximumExceptionMessageLength];
}
