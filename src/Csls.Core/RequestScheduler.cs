using System.Threading.Channels;

namespace Csls.Core;

/// <summary>
/// Executes bounded read, write, and background work with fair mutation ordering.
/// </summary>
public sealed class RequestScheduler : IAsyncDisposable
{
    private readonly Channel<(RequestMode Mode, Func<Task> Execute)> _queue;
    private readonly SemaphoreSlim _foregroundConcurrency;
    private readonly SemaphoreSlim _backgroundConcurrency;
    private readonly ValueTask _processingTask;
    private long _nextOrdinal;
    private int _disposeState;

    /// <summary>
    /// Initializes a bounded scheduler with explicit foreground and background limits.
    /// </summary>
    /// <param name="capacity">The maximum number of queued operations.</param>
    /// <param name="foregroundConcurrency">The maximum number of concurrent foreground reads.</param>
    /// <param name="backgroundConcurrency">The maximum number of concurrent background reads.</param>
    public RequestScheduler(
        int capacity = 256,
        int? foregroundConcurrency = null,
        int? backgroundConcurrency = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        int foregroundLimit = foregroundConcurrency ?? Math.Max(1, Environment.ProcessorCount);
        int backgroundLimit = backgroundConcurrency ?? Math.Max(1, Environment.ProcessorCount / 2);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(foregroundLimit);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(backgroundLimit);

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
    /// Queues a typed operation and returns its eventual result.
    /// </summary>
    /// <typeparam name="T">The operation result type.</typeparam>
    /// <param name="mode">The operation concurrency mode.</param>
    /// <param name="workspaceGenerationProvider">Returns the generation after earlier mutations retire.</param>
    /// <param name="operation">The operation to execute.</param>
    /// <param name="cancellationToken">The peer cancellation token.</param>
    /// <returns>The operation result.</returns>
    public async Task<T> ScheduleAsync<T>(
        RequestMode mode,
        Func<long> workspaceGenerationProvider,
        Func<RequestContext, ValueTask<T>> operation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(workspaceGenerationProvider);
        ArgumentNullException.ThrowIfNull(operation);
        ObjectDisposedException.ThrowIf(IsStopping, this);

        long ordinal = Interlocked.Increment(ref _nextOrdinal);
        var admission = new TaskCompletionSource<RequestContext>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var retirement = Channel.CreateBounded<bool>(1);
        using CancellationTokenRegistration cancellationRegistration = cancellationToken.Register(
            static state =>
            {
                (TaskCompletionSource<RequestContext> source, CancellationToken token) =
                    ((TaskCompletionSource<RequestContext>, CancellationToken))state!;
                source.TrySetCanceled(token);
            },
            (admission, cancellationToken));

        async Task ExecuteAsync()
        {
            if (cancellationToken.IsCancellationRequested)
            {
                admission.TrySetCanceled(cancellationToken);
                return;
            }

            var context = new RequestContext(
                ordinal,
                Guid.NewGuid(),
                workspaceGenerationProvider(),
                cancellationToken);
            admission.TrySetResult(context);
            await retirement.Reader.ReadAsync(CancellationToken.None).ConfigureAwait(false);
        }

        await _queue.Writer
            .WriteAsync((mode, ExecuteAsync), cancellationToken)
            .ConfigureAwait(false);

        RequestContext context = await admission.Task.ConfigureAwait(false);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            return await operation(context).ConfigureAwait(false);
        }
        finally
        {
            retirement.Writer.TryWrite(true);
        }
    }

    /// <summary>
    /// Stops accepting work and waits for all accepted operations to retire.
    /// </summary>
    /// <returns>A value task that completes after scheduler shutdown.</returns>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposeState, 1) != 0)
        {
            return;
        }

        _queue.Writer.TryComplete();
        await _processingTask.ConfigureAwait(false);
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
                    await _foregroundConcurrency.WaitAsync().ConfigureAwait(false);
                    foregroundTasks.Add(RunWithSemaphoreAsync(execute, _foregroundConcurrency));
                    break;
                case RequestMode.ReadWrite:
                    await Task.WhenAll(foregroundTasks).ConfigureAwait(false);
                    foregroundTasks.Clear();
                    await execute().ConfigureAwait(false);
                    break;
                case RequestMode.ReadOnlyBackground:
                    await _backgroundConcurrency.WaitAsync().ConfigureAwait(false);
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
        try
        {
            await Task.Run(operation, CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            semaphore.Release();
        }
    }
}
