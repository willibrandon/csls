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
            RequestMode.ReadOnly,
            static () => 1,
            FirstReadAsync,
            cancellationToken);
        await firstReadStarted.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        Task<int> mutation = scheduler.ScheduleAsync(
            RequestMode.ReadWrite,
            static () => 1,
            MutationAsync,
            cancellationToken);
        Task<int> followingRead = scheduler.ScheduleAsync(
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
                RequestMode.ReadOnly,
                static () => 1,
                FirstReadAsync,
                cancellationToken),
            scheduler.ScheduleAsync(
                RequestMode.ReadOnly,
                static () => 1,
                SecondReadAsync,
                cancellationToken),
            scheduler.ScheduleAsync(
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
}
