namespace Csls.Core;

/// <summary>
/// Owns cancellation and mutable lifecycle state for one scheduled request.
/// </summary>
internal sealed class RequestActivityState
{
    private readonly Lock _gate = new();
    private readonly CancellationTokenSource _cancellationSource;
    private readonly TimeProvider _timeProvider;
    private TaskCompletionSource? _retirement;
    private RequestTraceRecord? _traceRecord;
    private Guid? _enrolledTraceId;
    private long? _workspaceGeneration;
    private long? _startedTimestamp;
    private DateTimeOffset? _startedAt;
    private bool _isCancellationRequested;
    private bool _isCompleted;
    private RequestExecutionStatus _status = RequestExecutionStatus.Queued;

    /// <summary>
    /// Initializes lifecycle state for one received request.
    /// </summary>
    /// <param name="ordinal">The monotonically increasing receive ordinal.</param>
    /// <param name="correlationId">The stable request correlation identifier.</param>
    /// <param name="name">The protocol or control operation name.</param>
    /// <param name="mode">The scheduler concurrency mode.</param>
    /// <param name="acceptedAt">The wall-clock receive time.</param>
    /// <param name="acceptedTimestamp">The monotonic receive timestamp.</param>
    /// <param name="timeProvider">The scheduler time provider.</param>
    /// <param name="cancellationToken">The peer cancellation token.</param>
    internal RequestActivityState(
        long ordinal,
        Guid correlationId,
        string name,
        RequestMode mode,
        DateTimeOffset acceptedAt,
        long acceptedTimestamp,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        Ordinal = ordinal;
        CorrelationId = correlationId;
        Name = name;
        Mode = mode;
        AcceptedAt = acceptedAt;
        AcceptedTimestamp = acceptedTimestamp;
        _cancellationSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _timeProvider = timeProvider;
    }

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
    /// Gets the monotonic request receive timestamp.
    /// </summary>
    internal long AcceptedTimestamp { get; }

    /// <summary>
    /// Gets the linked peer and server cancellation token.
    /// </summary>
    internal CancellationToken CancellationToken => _cancellationSource.Token;

    /// <summary>
    /// Attempts to request cancellation before the operation retires.
    /// </summary>
    /// <returns>True when cancellation was delivered to a live request.</returns>
    internal async Task<bool> TryCancelAsync()
    {
        Task cancellationTask;
        Task retirementTask;
        lock (_gate)
        {
            if (_isCompleted)
            {
                return false;
            }

            _isCancellationRequested = true;
            _traceRecord?.MarkCancellationRequested();
            _retirement ??= new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            retirementTask = _retirement.Task;
            cancellationTask = _cancellationSource.CancelAsync();
        }

        await cancellationTask.ConfigureAwait(false);
        await retirementTask.ConfigureAwait(false);
        return true;
    }

    /// <summary>
    /// Marks the request as executing against one workspace generation.
    /// </summary>
    /// <param name="workspaceGeneration">The captured workspace generation.</param>
    internal void MarkStarted(long workspaceGeneration)
    {
        DateTimeOffset startedAt = _timeProvider.GetUtcNow();
        long startedTimestamp = _timeProvider.GetTimestamp();
        lock (_gate)
        {
            if (_isCompleted)
            {
                return;
            }

            _workspaceGeneration = workspaceGeneration;
            _startedAt = startedAt;
            _startedTimestamp = startedTimestamp;
            _status = RequestExecutionStatus.Running;
            _traceRecord?.MarkStarted(workspaceGeneration, startedAt, startedTimestamp);
        }
    }

    /// <summary>
    /// Attaches one active bounded trace record to the request.
    /// </summary>
    /// <param name="traceId">The active trace identifier.</param>
    /// <returns>The new mutable trace record, or null when already enrolled.</returns>
    internal RequestTraceRecord? TryAttachTrace(Guid traceId)
    {
        lock (_gate)
        {
            if (_enrolledTraceId == traceId)
            {
                return null;
            }

            var record = new RequestTraceRecord(
                traceId,
                CreateSnapshot(),
                _timeProvider,
                AcceptedTimestamp,
                _startedTimestamp);
            _enrolledTraceId = traceId;
            _traceRecord = record;
            return record;
        }
    }

    /// <summary>
    /// Detaches a trace record when its trace session stops or evicts it.
    /// </summary>
    /// <param name="traceId">The trace identifier to detach.</param>
    internal void DetachTrace(Guid traceId)
    {
        lock (_gate)
        {
            if (_traceRecord?.TraceId == traceId)
            {
                _traceRecord = null;
            }
        }
    }

    /// <summary>
    /// Records one terminal state and releases peer cancellation registration.
    /// </summary>
    /// <param name="status">The terminal lifecycle status.</param>
    /// <param name="exception">The exception that ended a failed request.</param>
    /// <returns>True when this call completed the request.</returns>
    internal bool Complete(RequestExecutionStatus status, Exception? exception)
    {
        DateTimeOffset completedAt = _timeProvider.GetUtcNow();
        long completedTimestamp = _timeProvider.GetTimestamp();
        lock (_gate)
        {
            if (_isCompleted)
            {
                return false;
            }

            CompleteCore(status, exception, completedAt, completedTimestamp);
            return true;
        }
    }

    /// <summary>
    /// Retires a canceled request that has not started executing.
    /// </summary>
    /// <param name="completeRequest">Completes the public request before retirement is signaled.</param>
    /// <returns>True when this call retired the queued request.</returns>
    internal bool CompleteQueuedCancellation(Action completeRequest)
    {
        ArgumentNullException.ThrowIfNull(completeRequest);
        DateTimeOffset completedAt = _timeProvider.GetUtcNow();
        long completedTimestamp = _timeProvider.GetTimestamp();
        lock (_gate)
        {
            if (_isCompleted || _status != RequestExecutionStatus.Queued)
            {
                return false;
            }

            completeRequest();
            CompleteCore(
                RequestExecutionStatus.Canceled,
                exception: null,
                completedAt,
                completedTimestamp);
            return true;
        }
    }

    /// <summary>
    /// Creates one immutable observation of the current request lifecycle.
    /// </summary>
    /// <returns>The request activity observation.</returns>
    internal RequestActivitySnapshot GetSnapshot()
    {
        lock (_gate)
        {
            return CreateSnapshot();
        }
    }

    private RequestActivitySnapshot CreateSnapshot() => new()
    {
        Ordinal = Ordinal,
        CorrelationId = CorrelationId,
        Name = Name,
        Mode = Mode,
        WorkspaceGeneration = _workspaceGeneration,
        AcceptedAt = AcceptedAt,
        StartedAt = _startedAt,
        Duration = _timeProvider.GetElapsedTime(
            _startedTimestamp ?? AcceptedTimestamp,
            _timeProvider.GetTimestamp()),
        IsCancellationRequested = _isCancellationRequested,
        Status = _status
    };

    private void CompleteCore(
        RequestExecutionStatus status,
        Exception? exception,
        DateTimeOffset completedAt,
        long completedTimestamp)
    {
        _isCompleted = true;
        _status = status;
        _isCancellationRequested |= status == RequestExecutionStatus.Canceled;
        _traceRecord?.Complete(status, exception, completedAt, completedTimestamp);
        _cancellationSource.Dispose();
        _retirement?.TrySetResult();
    }
}
