using System.Collections.Concurrent;
using System.Threading.Channels;

namespace Csls.Core;

/// <summary>
/// Executes bounded read, write, and background work with fair mutation ordering.
/// </summary>
public sealed class RequestScheduler : IAsyncDisposable
{
    private const int ActivitySnapshotCapacity = 512;
    private readonly Channel<(RequestMode Mode, Func<Task> Execute)> _queue;
    private readonly SemaphoreSlim _foregroundConcurrency;
    private readonly SemaphoreSlim _backgroundConcurrency;
    private readonly ValueTask _processingTask;
    private readonly ConcurrentDictionary<Guid, RequestActivityState> _requests = new();
    private readonly ConcurrentDictionary<string, RequestStatisticsState> _statistics = new(
        StringComparer.Ordinal);
    private readonly Dictionary<long, Task> _requestCompletions = [];
    private readonly Lock _lifecycleGate = new();
    private readonly Lock _traceGate = new();
    private readonly Queue<(RequestActivityState State, RequestTraceRecord Record)> _traceRecords = new();
    private readonly TimeProvider _timeProvider;
    private readonly int _capacity;
    private readonly int _foregroundLimit;
    private readonly int _backgroundLimit;
    private readonly int _traceCapacity;
    private Guid? _traceId;
    private DateTimeOffset? _traceStartedAt;
    private DateTimeOffset? _traceStoppedAt;
    private long _traceDroppedEntries;
    private bool _isTraceActive;
    private long _nextOrdinal;
    private long _acceptedRequests;
    private long _completedRequests;
    private int _queuedRequests;
    private int _activeForegroundRequests;
    private int _activeBackgroundRequests;
    private int _mutationActive;
    private int _disposeState;

    /// <summary>
    /// Initializes a bounded scheduler with explicit foreground and background limits.
    /// </summary>
    /// <param name="capacity">The maximum number of queued operations.</param>
    /// <param name="foregroundConcurrency">The maximum number of concurrent foreground reads.</param>
    /// <param name="backgroundConcurrency">The maximum number of concurrent background reads.</param>
    /// <param name="traceCapacity">The maximum number of retained trace records.</param>
    /// <param name="timeProvider">The time provider used for lifecycle observations.</param>
    public RequestScheduler(
        int capacity = 256,
        int? foregroundConcurrency = null,
        int? backgroundConcurrency = null,
        int traceCapacity = 1024,
        TimeProvider? timeProvider = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(traceCapacity);
        int foregroundLimit = foregroundConcurrency ?? capacity;
        int backgroundLimit = backgroundConcurrency ?? capacity;
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(foregroundLimit);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(backgroundLimit);

        _capacity = capacity;
        _foregroundLimit = foregroundLimit;
        _backgroundLimit = backgroundLimit;
        _traceCapacity = traceCapacity;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _queue = Channel.CreateBounded<(RequestMode, Func<Task>)>(new BoundedChannelOptions(capacity)
        {
            AllowSynchronousContinuations = false,
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false
        });
        _foregroundConcurrency = new SemaphoreSlim(foregroundLimit, foregroundLimit);
        _backgroundConcurrency = new SemaphoreSlim(backgroundLimit, backgroundLimit);
        _processingTask = new ValueTask(ProcessAsync());
    }

    /// <summary>
    /// Gets whether the scheduler has started shutting down.
    /// </summary>
    public bool IsStopping => Volatile.Read(ref _disposeState) != 0;

    /// <summary>
    /// Gets one lock-free observation of scheduler capacity and activity.
    /// </summary>
    /// <returns>The current scheduler counters and limits.</returns>
    public RequestSchedulerSnapshot GetSnapshot()
    {
        RequestActivityState[] retainedRequests = GetNewestRequestStates(
            ActivitySnapshotCapacity,
            out int totalActiveRequests);
        var activeRequests = new RequestActivitySnapshot[retainedRequests.Length];
        for (int index = 0; index < retainedRequests.Length; index++)
        {
            activeRequests[index] = retainedRequests[index].GetSnapshot();
        }

        return new RequestSchedulerSnapshot
        {
            ActivityCapacity = ActivitySnapshotCapacity,
            Capacity = _capacity,
            ForegroundConcurrency = _foregroundLimit,
            BackgroundConcurrency = _backgroundLimit,
            AcceptedRequests = Interlocked.Read(ref _acceptedRequests),
            CompletedRequests = Interlocked.Read(ref _completedRequests),
            QueuedRequests = Volatile.Read(ref _queuedRequests),
            ActiveForegroundRequests = Volatile.Read(ref _activeForegroundRequests),
            ActiveBackgroundRequests = Volatile.Read(ref _activeBackgroundRequests),
            IsMutationActive = Volatile.Read(ref _mutationActive) != 0,
            IsStopping = IsStopping,
            TotalActiveRequests = totalActiveRequests,
            ActiveRequestsTruncated = totalActiveRequests > activeRequests.Length,
            ActiveRequests = activeRequests,
            Statistics =
            [
                .. _statistics
                    .Select(static pair => pair.Value.GetSnapshot(pair.Key))
                    .OrderByDescending(static item => item.AverageDuration)
                    .ThenBy(static item => item.Name, StringComparer.Ordinal)
            ]
        };
    }

    /// <summary>
    /// Attempts to cancel one queued or running request by correlation identifier.
    /// </summary>
    /// <param name="correlationId">The stable request correlation identifier.</param>
    /// <returns>True when cancellation was delivered to a live request.</returns>
    public Task<bool> TryCancelAsync(Guid correlationId) =>
        _requests.TryGetValue(correlationId, out RequestActivityState? request)
            ? request.TryCancelAsync()
            : Task.FromResult(false);

    /// <summary>
    /// Starts one bounded request trace and enrolls requests that are already active.
    /// </summary>
    /// <returns>The newly active trace observation.</returns>
    public RequestTraceSnapshot StartTrace()
    {
        lock (_traceGate)
        {
            if (_isTraceActive)
            {
                throw new InvalidOperationException("Request tracing is already active.");
            }

            DetachTraceRecords();
            _traceRecords.Clear();
            _traceDroppedEntries = 0;
            _traceId = Guid.NewGuid();
            _traceStartedAt = _timeProvider.GetUtcNow();
            _traceStoppedAt = null;
            _isTraceActive = true;
            RequestActivityState[] activeRequests = GetNewestRequestStates(
                _traceCapacity,
                out int totalActiveRequests);
            _traceDroppedEntries = totalActiveRequests - activeRequests.Length;
            foreach (RequestActivityState request in activeRequests)
            {
                EnrollTrace(request);
            }

            return CreateTraceSnapshot();
        }
    }

    /// <summary>
    /// Stops the active request trace and returns its final bounded observation.
    /// </summary>
    /// <returns>The stopped trace observation.</returns>
    public RequestTraceSnapshot StopTrace()
    {
        lock (_traceGate)
        {
            if (!_isTraceActive)
            {
                throw new InvalidOperationException("Request tracing is not active.");
            }

            _isTraceActive = false;
            _traceStoppedAt = _timeProvider.GetUtcNow();
            DetachTraceRecords();
            return CreateTraceSnapshot();
        }
    }

    /// <summary>
    /// Gets the current or most recently stopped bounded request trace.
    /// </summary>
    /// <returns>The current trace observation.</returns>
    public RequestTraceSnapshot GetTraceSnapshot()
    {
        lock (_traceGate)
        {
            return CreateTraceSnapshot();
        }
    }

    /// <summary>
    /// Queues a typed operation and returns its eventual result.
    /// </summary>
    /// <typeparam name="T">The operation result type.</typeparam>
    /// <param name="name">The protocol or control operation name.</param>
    /// <param name="mode">The operation concurrency mode.</param>
    /// <param name="workspaceGenerationProvider">Returns the generation after earlier mutations retire.</param>
    /// <param name="operation">The operation to execute.</param>
    /// <param name="cancellationToken">The peer cancellation token.</param>
    /// <returns>The operation result.</returns>
    public Task<T> ScheduleAsync<T>(
        string name,
        RequestMode mode,
        Func<long> workspaceGenerationProvider,
        Func<RequestContext, ValueTask<T>> operation,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(workspaceGenerationProvider);
        ArgumentNullException.ThrowIfNull(operation);
        var completion = new TaskCompletionSource<T>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_lifecycleGate)
        {
            ObjectDisposedException.ThrowIf(IsStopping, this);
            long ordinal = Interlocked.Increment(ref _nextOrdinal);
            var correlationId = Guid.NewGuid();
            var request = new RequestActivityState(
                ordinal,
                correlationId,
                name,
                mode,
                _timeProvider.GetUtcNow(),
                _timeProvider.GetTimestamp(),
                _timeProvider,
                cancellationToken);
            if (!_requests.TryAdd(correlationId, request))
            {
                request.Complete(RequestExecutionStatus.Failed, null);
                throw new InvalidOperationException(
                    "A duplicate request correlation identifier was generated.");
            }

            _requestCompletions.Add(ordinal, completion.Task);
            _ = RunScheduledRequestAsync(
                ordinal,
                correlationId,
                mode,
                workspaceGenerationProvider,
                operation,
                request,
                completion);
        }

        return completion.Task;
    }

    private async Task RunScheduledRequestAsync<T>(
        long ordinal,
        Guid correlationId,
        RequestMode mode,
        Func<long> workspaceGenerationProvider,
        Func<RequestContext, ValueTask<T>> operation,
        RequestActivityState request,
        TaskCompletionSource<T> completion)
    {
        try
        {
            await RunScheduledRequestCoreAsync(
                ordinal,
                correlationId,
                mode,
                workspaceGenerationProvider,
                operation,
                request,
                completion).ConfigureAwait(false);
        }
        finally
        {
            lock (_lifecycleGate)
            {
                _requestCompletions.Remove(ordinal);
            }
        }
    }

    private async Task RunScheduledRequestCoreAsync<T>(
        long ordinal,
        Guid correlationId,
        RequestMode mode,
        Func<long> workspaceGenerationProvider,
        Func<RequestContext, ValueTask<T>> operation,
        RequestActivityState request,
        TaskCompletionSource<T> completion)
    {
        CancellationToken requestCancellationToken = request.CancellationToken;
        var admission = new TaskCompletionSource<RequestContext>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var retirement = Channel.CreateBounded<bool>(1);
        using CancellationTokenRegistration cancellationRegistration = requestCancellationToken.Register(
            static state =>
            {
                (
                    TaskCompletionSource<RequestContext> source,
                    CancellationToken token,
                    RequestScheduler scheduler,
                    RequestActivityState request) =
                    ((
                        TaskCompletionSource<RequestContext>,
                        CancellationToken,
                        RequestScheduler,
                        RequestActivityState))state!;
                source.TrySetCanceled(token);
                scheduler.CompleteQueuedCancellation(request);
            },
            (admission, requestCancellationToken, this, request));

        async Task ExecuteAsync()
        {
            Interlocked.Decrement(ref _queuedRequests);
            if (requestCancellationToken.IsCancellationRequested)
            {
                admission.TrySetCanceled(requestCancellationToken);
                CompleteRequest(request, RequestExecutionStatus.Canceled, null);
                Interlocked.Increment(ref _completedRequests);
                return;
            }

            long workspaceGeneration = workspaceGenerationProvider();
            IncrementActive(mode);
            request.MarkStarted(workspaceGeneration);
            var context = new RequestContext(
                ordinal,
                correlationId,
                workspaceGeneration,
                requestCancellationToken);
            if (!admission.TrySetResult(context))
            {
                CompleteRequest(request, RequestExecutionStatus.Canceled, null);
                DecrementActive(mode);
                Interlocked.Increment(ref _completedRequests);
                return;
            }

            try
            {
                await retirement.Reader.ReadAsync(CancellationToken.None).ConfigureAwait(false);
            }
            finally
            {
                DecrementActive(mode);
                Interlocked.Increment(ref _completedRequests);
            }
        }

        Interlocked.Increment(ref _queuedRequests);
        try
        {
            await _queue.Writer
                .WriteAsync((mode, ExecuteAsync), requestCancellationToken)
                .ConfigureAwait(false);
            Interlocked.Increment(ref _acceptedRequests);
            EnrollCurrentTrace(request);
        }
        catch (Exception exception) when (IsRecoverableFailure(exception))
        {
            Interlocked.Decrement(ref _queuedRequests);
            bool canceled = requestCancellationToken.IsCancellationRequested;
            CompleteRequest(
                request,
                canceled
                    ? RequestExecutionStatus.Canceled
                    : RequestExecutionStatus.Failed,
                exception);
            if (canceled)
            {
                completion.TrySetCanceled(requestCancellationToken);
            }
            else
            {
                completion.TrySetException(exception);
            }

            return;
        }

        RequestContext context;
        try
        {
            context = await admission.Task.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
            when (requestCancellationToken.IsCancellationRequested)
        {
            completion.TrySetCanceled(requestCancellationToken);
            return;
        }

        RequestExecutionStatus status = RequestExecutionStatus.Failed;
        Exception? failure = null;
        long executionStartedTimestamp = _timeProvider.GetTimestamp();
        try
        {
            requestCancellationToken.ThrowIfCancellationRequested();
            T result = await operation(context).ConfigureAwait(false);
            status = RequestExecutionStatus.Succeeded;
            completion.TrySetResult(result);
        }
        catch (OperationCanceledException exception)
            when (requestCancellationToken.IsCancellationRequested)
        {
            status = RequestExecutionStatus.Canceled;
            failure = exception;
            completion.TrySetCanceled(requestCancellationToken);
        }
        catch (Exception exception) when (IsRecoverableFailure(exception))
        {
            status = RequestExecutionStatus.Failed;
            failure = exception;
            completion.TrySetException(exception);
        }
        finally
        {
            _statistics
                .GetOrAdd(request.Name, static _ => new RequestStatisticsState())
                .Record(_timeProvider.GetElapsedTime(executionStartedTimestamp));
            CompleteRequest(request, status, failure);
            retirement.Writer.TryWrite(true);
        }
    }

    /// <summary>
    /// Stops accepting work, cancels outstanding operations, and waits for retirement.
    /// </summary>
    /// <returns>A value task that completes after scheduler shutdown.</returns>
    public async ValueTask DisposeAsync()
    {
        RequestActivityState[] requests;
        Task[] requestCompletions;
        lock (_lifecycleGate)
        {
            if (Interlocked.Exchange(ref _disposeState, 1) != 0)
            {
                return;
            }

            _queue.Writer.TryComplete();
            requests = [.. _requests.Values];
            requestCompletions = [.. _requestCompletions.Values];
        }

        Task<bool>[] cancellationTasks =
        [
            .. requests.Select(static request => request.TryCancelAsync())
        ];
        await Task.WhenAll(cancellationTasks).ConfigureAwait(false);
        await _processingTask.ConfigureAwait(false);
        await Task.WhenAll(requestCompletions)
            .ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        _foregroundConcurrency.Dispose();
        _backgroundConcurrency.Dispose();
        GC.SuppressFinalize(this);
    }

    private async Task ProcessAsync()
    {
        var foregroundTasks = new List<Task>();
        var backgroundTasks = new List<Task>();

        await foreach ((RequestMode mode, Func<Task> execute) in
            _queue.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            foregroundTasks.RemoveAll(static task => task.IsCompleted);
            backgroundTasks.RemoveAll(static task => task.IsCompleted);

            switch (mode)
            {
                case RequestMode.ReadOnly:
                    foregroundTasks.Add(RunWithSemaphoreAsync(execute, _foregroundConcurrency));
                    break;
                case RequestMode.ReadWrite:
                    await Task.WhenAll(foregroundTasks).ConfigureAwait(false);
                    foregroundTasks.Clear();
                    await execute().ConfigureAwait(false);
                    break;
                case RequestMode.ReadOnlyBackground:
                    backgroundTasks.Add(RunWithSemaphoreAsync(execute, _backgroundConcurrency));
                    break;
                default:
                    throw new InvalidOperationException($"Unknown request mode: {mode}.");
            }
        }

        await Task.WhenAll(foregroundTasks).ConfigureAwait(false);
        await Task.WhenAll(backgroundTasks).ConfigureAwait(false);
    }

    private static async Task RunWithSemaphoreAsync(Func<Task> operation, SemaphoreSlim semaphore)
    {
        await semaphore.WaitAsync().ConfigureAwait(false);
        try
        {
            await operation().ConfigureAwait(false);
        }
        finally
        {
            semaphore.Release();
        }
    }

    private void IncrementActive(RequestMode mode)
    {
        switch (mode)
        {
            case RequestMode.ReadOnly:
                Interlocked.Increment(ref _activeForegroundRequests);
                break;
            case RequestMode.ReadOnlyBackground:
                Interlocked.Increment(ref _activeBackgroundRequests);
                break;
            case RequestMode.ReadWrite:
                Volatile.Write(ref _mutationActive, 1);
                break;
            default:
                throw new InvalidOperationException($"Unknown request mode: {mode}.");
        }
    }

    private static bool IsRecoverableFailure(Exception exception) =>
        exception is not OutOfMemoryException;

    private void DecrementActive(RequestMode mode)
    {
        switch (mode)
        {
            case RequestMode.ReadOnly:
                Interlocked.Decrement(ref _activeForegroundRequests);
                break;
            case RequestMode.ReadOnlyBackground:
                Interlocked.Decrement(ref _activeBackgroundRequests);
                break;
            case RequestMode.ReadWrite:
                Volatile.Write(ref _mutationActive, 0);
                break;
            default:
                throw new InvalidOperationException($"Unknown request mode: {mode}.");
        }
    }

    private void CompleteRequest(
        RequestActivityState request,
        RequestExecutionStatus status,
        Exception? exception)
    {
        if (request.Complete(status, exception))
        {
            _requests.TryRemove(request.CorrelationId, out _);
        }
    }

    private void CompleteQueuedCancellation(RequestActivityState request)
    {
        if (request.CompleteQueuedCancellation())
        {
            _requests.TryRemove(request.CorrelationId, out _);
        }
    }

    private void EnrollCurrentTrace(RequestActivityState request)
    {
        lock (_traceGate)
        {
            if (_isTraceActive)
            {
                EnrollTrace(request);
            }
        }
    }

    private void EnrollTrace(RequestActivityState request)
    {
        Guid traceId = _traceId ??
            throw new InvalidOperationException("An active trace must have an identifier.");
        RequestTraceRecord? record = request.TryAttachTrace(traceId);
        if (record is null)
        {
            return;
        }

        _traceRecords.Enqueue((request, record));
        if (_traceRecords.Count > _traceCapacity)
        {
            (RequestActivityState State, RequestTraceRecord Record) = _traceRecords.Dequeue();
            State.DetachTrace(Record.TraceId);
            _traceDroppedEntries++;
        }
    }

    private void DetachTraceRecords()
    {
        foreach ((RequestActivityState state, RequestTraceRecord record) in _traceRecords)
        {
            state.DetachTrace(record.TraceId);
        }
    }

    private RequestTraceSnapshot CreateTraceSnapshot() => new()
    {
        IsActive = _isTraceActive,
        TraceId = _traceId,
        StartedAt = _traceStartedAt,
        StoppedAt = _traceStoppedAt,
        Capacity = _traceCapacity,
        DroppedEntries = _traceDroppedEntries,
        Entries =
        [
            .. _traceRecords.Select(static item => item.Record.GetSnapshot())
        ]
    };

    private RequestActivityState[] GetNewestRequestStates(int capacity, out int totalCount)
    {
        var retained = new PriorityQueue<RequestActivityState, long>(capacity);
        totalCount = 0;
        foreach (RequestActivityState request in _requests.Values)
        {
            totalCount++;
            if (retained.Count < capacity)
            {
                retained.Enqueue(request, request.Ordinal);
                continue;
            }

            retained.TryPeek(out _, out long oldestOrdinal);
            if (request.Ordinal > oldestOrdinal)
            {
                retained.DequeueEnqueue(request, request.Ordinal);
            }
        }

        var requests = new RequestActivityState[retained.Count];
        int index = 0;
        foreach ((RequestActivityState element, _) in retained.UnorderedItems)
        {
            requests[index++] = element;
        }

        Array.Sort(
            requests,
            static (left, right) => left.Ordinal.CompareTo(right.Ordinal));
        return requests;
    }
}
