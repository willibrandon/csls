using Csls.Core;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;

namespace Csls.Tests;

/// <summary>
/// Verifies request ordering and concurrency through the real bounded scheduler.
/// </summary>
[TestClass]
public sealed class RequestSchedulerTests
{
    /// <summary>
    /// Gets the active MSTest context and its framework-managed cancellation token.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// Verifies a mutation waits for an earlier read and blocks a later read.
    /// </summary>
    [TestMethod]
    public async Task MutationWaitsForEarlierReadAndPrecedesLaterRead()
    {
        CancellationToken cancellationToken = TestContext.CancellationToken;
        var scheduler = new RequestScheduler(
            capacity: 8,
            foregroundConcurrency: 2,
            backgroundConcurrency: 1);
        await using ConfiguredAsyncDisposable schedulerDisposal =
            scheduler.ConfigureAwait(false);
        var executionOrder = new ConcurrentQueue<string>();
        var firstReadStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirstRead = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var mutationStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseMutation = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var followingReadStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);

        Task<int> firstRead = scheduler.ScheduleAsync(
            "first-read",
            RequestMode.ReadOnly,
            static () => 1,
            FirstReadAsync,
            cancellationToken);
        await firstReadStarted.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        Task<int> mutation = scheduler.ScheduleAsync(
            "mutation",
            RequestMode.ReadWrite,
            static () => 1,
            MutationAsync,
            cancellationToken);
        Task<int> followingRead = scheduler.ScheduleAsync(
            "following-read",
            RequestMode.ReadOnly,
            static () => 1,
            FollowingReadAsync,
            cancellationToken);

        releaseFirstRead.SetResult();
        await mutationStarted.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        Assert.IsFalse(followingReadStarted.Task.IsCompleted);
        releaseMutation.SetResult();

        int[] results = await Task.WhenAll(firstRead, mutation, followingRead)
            .ConfigureAwait(false);
        Assert.AreEqual(1, results[0]);
        Assert.AreEqual(2, results[1]);
        Assert.AreEqual(3, results[2]);
        string[] observedOrder = [.. executionOrder];
        Assert.HasCount(4, observedOrder);
        Assert.AreEqual("read-start", observedOrder[0]);
        Assert.AreEqual("read-end", observedOrder[1]);
        Assert.AreEqual("write", observedOrder[2]);
        Assert.AreEqual("following-read", observedOrder[3]);
        return;

        async ValueTask<int> FirstReadAsync(RequestContext context)
        {
            executionOrder.Enqueue("read-start");
            firstReadStarted.SetResult();
            await releaseFirstRead.Task.WaitAsync(context.CancellationToken).ConfigureAwait(false);
            executionOrder.Enqueue("read-end");
            return 1;
        }

        async ValueTask<int> MutationAsync(RequestContext context)
        {
            executionOrder.Enqueue("write");
            mutationStarted.SetResult();
            await releaseMutation.Task.WaitAsync(context.CancellationToken).ConfigureAwait(false);
            return 2;
        }

        ValueTask<int> FollowingReadAsync(RequestContext context)
        {
            context.CancellationToken.ThrowIfCancellationRequested();
            executionOrder.Enqueue("following-read");
            followingReadStarted.SetResult();
            return ValueTask.FromResult(3);
        }
    }

    /// <summary>
    /// Verifies foreground reads never exceed the configured concurrency limit.
    /// </summary>
    [TestMethod]
    public async Task ForegroundReadsRespectConfiguredConcurrency()
    {
        CancellationToken cancellationToken = TestContext.CancellationToken;
        var scheduler = new RequestScheduler(
            capacity: 8,
            foregroundConcurrency: 2,
            backgroundConcurrency: 1);
        await using ConfiguredAsyncDisposable schedulerDisposal =
            scheduler.ConfigureAwait(false);
        var firstReadStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var secondReadStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var thirdStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseReads = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        int activeReads = 0;
        int maximumActiveReads = 0;

        Task<int>[] reads =
        [
            scheduler.ScheduleAsync(
                "first-read",
                RequestMode.ReadOnly,
                static () => 1,
                FirstReadAsync,
                cancellationToken),
            scheduler.ScheduleAsync(
                "second-read",
                RequestMode.ReadOnly,
                static () => 1,
                SecondReadAsync,
                cancellationToken),
            scheduler.ScheduleAsync(
                "third-read",
                RequestMode.ReadOnly,
                static () => 1,
                ThirdReadAsync,
                cancellationToken)
        ];

        await Task.WhenAll(firstReadStarted.Task, secondReadStarted.Task)
            .WaitAsync(cancellationToken).ConfigureAwait(false);
        Assert.AreEqual(2, Volatile.Read(ref activeReads));
        Assert.IsFalse(thirdStarted.Task.IsCompleted);

        releaseReads.SetResult();
        int[] results = await Task.WhenAll(reads).ConfigureAwait(false);
        Assert.AreEqual(2, maximumActiveReads);
        Assert.AreEqual(1, results[0]);
        Assert.AreEqual(2, results[1]);
        Assert.AreEqual(3, results[2]);
        return;

        ValueTask<int> FirstReadAsync(RequestContext context) =>
            ReadAsync(context, firstReadStarted, 1);

        ValueTask<int> SecondReadAsync(RequestContext context) =>
            ReadAsync(context, secondReadStarted, 2);

        ValueTask<int> ThirdReadAsync(RequestContext context) =>
            ReadAsync(context, thirdStarted, 3);

        async ValueTask<int> ReadAsync(
            RequestContext context,
            TaskCompletionSource started,
            int result)
        {
            int currentReads = Interlocked.Increment(ref activeReads);
            int observedMaximum;
            do
            {
                observedMaximum = Volatile.Read(ref maximumActiveReads);
            }
            while (currentReads > observedMaximum &&
                Interlocked.CompareExchange(
                    ref maximumActiveReads,
                    currentReads,
                    observedMaximum) != observedMaximum);

            started.SetResult();

            try
            {
                await releaseReads.Task.WaitAsync(context.CancellationToken).ConfigureAwait(false);
                return result;
            }
            finally
            {
                Interlocked.Decrement(ref activeReads);
            }
        }
    }

    /// <summary>
    /// Verifies request identity, server cancellation, and bounded lifecycle tracing.
    /// </summary>
    [TestMethod]
    public async Task SchedulerTracksCancellationAndTraceLifecycle()
    {
        CancellationToken cancellationToken = TestContext.CancellationToken;
        var scheduler = new RequestScheduler(
            capacity: 4,
            foregroundConcurrency: 1,
            backgroundConcurrency: 1,
            traceCapacity: 2);
        var firstStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);

        RequestTraceSnapshot startedTrace = scheduler.StartTrace();
        Assert.IsTrue(startedTrace.IsActive);
        Assert.IsNotNull(startedTrace.TraceId);
        Assert.IsEmpty(startedTrace.Entries);
        Assert.ThrowsExactly<InvalidOperationException>(() => scheduler.StartTrace());

        Task<int> first = scheduler.ScheduleAsync(
            "first-read",
            RequestMode.ReadOnly,
            static () => 7,
            FirstAsync,
            cancellationToken);
        await firstStarted.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        Task<int> second = scheduler.ScheduleAsync(
            "second-read",
            RequestMode.ReadOnly,
            static () => 7,
            SecondAsync,
            cancellationToken);

        RequestSchedulerSnapshot activity = scheduler.GetSnapshot();
        Assert.AreEqual(2, activity.TotalActiveRequests);
        Assert.IsFalse(activity.ActiveRequestsTruncated);
        Assert.HasCount(2, activity.ActiveRequests);
        RequestActivitySnapshot running = activity.ActiveRequests[0];
        RequestActivitySnapshot queued = activity.ActiveRequests[1];
        Assert.AreEqual("first-read", running.Name);
        Assert.AreEqual(RequestExecutionStatus.Running, running.Status);
        Assert.AreEqual(7, running.WorkspaceGeneration);
        Assert.IsNotNull(running.StartedAt);
        Assert.AreEqual("second-read", queued.Name);
        Assert.AreEqual(RequestExecutionStatus.Queued, queued.Status);
        Assert.IsNull(queued.WorkspaceGeneration);
        Assert.IsNull(queued.StartedAt);
        Assert.AreNotEqual(running.CorrelationId, queued.CorrelationId);

        Assert.IsFalse(await scheduler.TryCancelAsync(Guid.NewGuid()).ConfigureAwait(false));
        Assert.IsTrue(await scheduler.TryCancelAsync(queued.CorrelationId).ConfigureAwait(false));
        TaskCanceledException? cancellationException = null;
        try
        {
            await second.ConfigureAwait(false);
        }
        catch (TaskCanceledException exception)
        {
            cancellationException = exception;
        }

        Assert.IsNotNull(cancellationException);
        RequestActivitySnapshot canceledQueued = scheduler.GetSnapshot().ActiveRequests[1];
        Assert.IsTrue(canceledQueued.IsCancellationRequested);

        releaseFirst.SetResult();
        Assert.AreEqual(1, await first.ConfigureAwait(false));
        await scheduler.DisposeAsync().ConfigureAwait(false);

        RequestTraceSnapshot stoppedTrace = scheduler.StopTrace();
        Assert.IsFalse(stoppedTrace.IsActive);
        Assert.AreEqual(startedTrace.TraceId, stoppedTrace.TraceId);
        Assert.IsNotNull(stoppedTrace.StartedAt);
        Assert.IsNotNull(stoppedTrace.StoppedAt);
        Assert.AreEqual(2, stoppedTrace.Capacity);
        Assert.AreEqual(0, stoppedTrace.DroppedEntries);
        Assert.HasCount(2, stoppedTrace.Entries);
        RequestTraceEntry completed = stoppedTrace.Entries[0];
        RequestTraceEntry canceled = stoppedTrace.Entries[1];
        Assert.AreEqual(running.CorrelationId, completed.CorrelationId);
        Assert.AreEqual(RequestExecutionStatus.Succeeded, completed.Status);
        Assert.IsNotNull(completed.CompletedAt);
        Assert.IsGreaterThanOrEqualTo(TimeSpan.Zero, completed.Duration);
        Assert.AreEqual(queued.CorrelationId, canceled.CorrelationId);
        Assert.AreEqual(RequestExecutionStatus.Canceled, canceled.Status);
        Assert.IsTrue(canceled.IsCancellationRequested);
        Assert.IsNull(canceled.StartedAt);
        Assert.IsNotNull(canceled.CompletedAt);
        Assert.ThrowsExactly<InvalidOperationException>(() => scheduler.StopTrace());
        return;

        async ValueTask<int> FirstAsync(RequestContext context)
        {
            firstStarted.SetResult();
            await releaseFirst.Task.WaitAsync(context.CancellationToken).ConfigureAwait(false);
            return 1;
        }

        static async ValueTask<int> SecondAsync(RequestContext context)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, context.CancellationToken)
                .ConfigureAwait(false);
            return 2;
        }
    }

    /// <summary>
    /// Bounds active observations and trace retention at their exact capacities.
    /// </summary>
    [TestMethod]
    public async Task SchedulerBoundsActivityAndTraceHistory()
    {
        CancellationToken cancellationToken = TestContext.CancellationToken;
        var scheduler = new RequestScheduler(
            capacity: 600,
            foregroundConcurrency: 1,
            backgroundConcurrency: 1,
            traceCapacity: 2);
        await using ConfiguredAsyncDisposable schedulerDisposal =
            scheduler.ConfigureAwait(false);
        var releaseRequests = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        RequestTraceSnapshot startedTrace = scheduler.StartTrace();
        var requests = new Task<long>[513];
        for (int index = 0; index < requests.Length; index++)
        {
            requests[index] = scheduler.ScheduleAsync(
                "bounded-read",
                RequestMode.ReadOnly,
                static () => 11,
                WaitForReleaseAsync,
                cancellationToken);
        }

        RequestSchedulerSnapshot activity;
        RequestTraceSnapshot activeTrace;
        try
        {
            activity = scheduler.GetSnapshot();
            activeTrace = scheduler.GetTraceSnapshot();
        }
        finally
        {
            releaseRequests.TrySetResult();
        }

        long[] results = await Task.WhenAll(requests).ConfigureAwait(false);
        RequestTraceSnapshot stoppedTrace = scheduler.StopTrace();
        Assert.AreEqual(513, activity.TotalActiveRequests);
        Assert.AreEqual(512, activity.ActivityCapacity);
        Assert.IsTrue(activity.ActiveRequestsTruncated);
        Assert.HasCount(512, activity.ActiveRequests);
        Assert.AreEqual(2, activity.ActiveRequests[0].Ordinal);
        Assert.AreEqual(513, activity.ActiveRequests[^1].Ordinal);
        Assert.AreEqual(startedTrace.TraceId, activeTrace.TraceId);
        Assert.IsTrue(activeTrace.IsActive);
        Assert.AreEqual(2, activeTrace.Capacity);
        Assert.AreEqual(511, activeTrace.DroppedEntries);
        Assert.HasCount(2, activeTrace.Entries);
        Assert.AreEqual(512, activeTrace.Entries[0].Ordinal);
        Assert.AreEqual(513, activeTrace.Entries[1].Ordinal);
        Assert.AreEqual(1, results[0]);
        Assert.AreEqual(513, results[^1]);
        Assert.AreEqual(511, stoppedTrace.DroppedEntries);
        Assert.IsTrue(stoppedTrace.Entries.All(static entry =>
            entry.Status == RequestExecutionStatus.Succeeded));
        return;

        async ValueTask<long> WaitForReleaseAsync(RequestContext context)
        {
            await releaseRequests.Task
                .WaitAsync(context.CancellationToken)
                .ConfigureAwait(false);
            return context.Ordinal;
        }
    }

    /// <summary>
    /// Runs cancellation callbacks outside request-state locks without losing state.
    /// </summary>
    [TestMethod]
    public async Task SchedulerCancellationCallbackCanInspectActivity()
    {
        CancellationToken cancellationToken = TestContext.CancellationToken;
        var scheduler = new RequestScheduler(
            capacity: 2,
            foregroundConcurrency: 1,
            backgroundConcurrency: 1);
        await using ConfiguredAsyncDisposable schedulerDisposal =
            scheduler.ConfigureAwait(false);
        var started = new TaskCompletionSource<Guid>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var callbackSnapshot = new TaskCompletionSource<RequestSchedulerSnapshot>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        Task<int> request = scheduler.ScheduleAsync(
            "callback-read",
            RequestMode.ReadOnly,
            static () => 13,
            WaitForCancellationAsync,
            cancellationToken);
        Guid correlationId = await started.Task
            .WaitAsync(cancellationToken)
            .ConfigureAwait(false);

        Assert.IsTrue(await scheduler.TryCancelAsync(correlationId)
            .WaitAsync(TimeSpan.FromSeconds(10), cancellationToken)
            .ConfigureAwait(false));
        RequestSchedulerSnapshot snapshot = await callbackSnapshot.Task
            .WaitAsync(cancellationToken)
            .ConfigureAwait(false);
        RequestActivitySnapshot activeRequest = snapshot.ActiveRequests.Single();
        Assert.AreEqual(correlationId, activeRequest.CorrelationId);
        Assert.IsTrue(activeRequest.IsCancellationRequested);
        TaskCanceledException? canceledRequest = null;
        try
        {
            await request.ConfigureAwait(false);
        }
        catch (TaskCanceledException exception)
        {
            canceledRequest = exception;
        }

        Assert.IsNotNull(canceledRequest);
        return;

        async ValueTask<int> WaitForCancellationAsync(RequestContext context)
        {
            using CancellationTokenRegistration registration =
                context.CancellationToken.Register(() =>
                    callbackSnapshot.TrySetResult(scheduler.GetSnapshot()));
            started.SetResult(context.CorrelationId);
            await Task.Delay(Timeout.InfiniteTimeSpan, context.CancellationToken)
                .ConfigureAwait(false);
            return 1;
        }
    }
}
