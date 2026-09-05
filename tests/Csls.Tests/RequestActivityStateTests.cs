using Csls.Core;

namespace Csls.Tests;

/// <summary>
/// Verifies request retirement while real peer cancellation callbacks are executing.
/// </summary>
[TestClass]
public sealed class RequestActivityStateTests
{
    /// <summary>
    /// Gets the active test context and its cancellation token.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// Releases the request lock before draining callbacks and signaling retirement.
    /// </summary>
    /// <param name="queued">Whether cancellation retires a request before execution.</param>
    /// <param name="status">The terminal request status to preserve.</param>
    [TestMethod]
    [DataRow(false, RequestExecutionStatus.Succeeded)]
    [DataRow(false, RequestExecutionStatus.Failed)]
    [DataRow(false, RequestExecutionStatus.Canceled)]
    [DataRow(true, RequestExecutionStatus.Canceled)]
    [Timeout(30_000, CooperativeCancellation = true)]
    public async Task CompletionReleasesStateLockBeforeDrainingPeerCancellation(
        bool queued,
        RequestExecutionStatus status)
    {
        CancellationToken cancellationToken = TestContext.CancellationToken;
        using var peer = new CancellationTokenSource();
        using var releaseCallback = new ManualResetEventSlim();
        var callbackEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var completingThread = new TaskCompletionSource<Thread>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var correlationId = Guid.NewGuid();
        var request = new RequestActivityState(
            1, correlationId, "retirement-read", RequestMode.ReadOnly,
            TimeProvider.System.GetUtcNow(), TimeProvider.System.GetTimestamp(),
            TimeProvider.System, peer.Token);
        if (!queued)
        {
            request.MarkStarted(23);
        }

        using CancellationTokenRegistration registration = request.CancellationToken.Register(() =>
        {
            callbackEntered.TrySetResult();
            releaseCallback.Wait(cancellationToken);
        });
        int queuedCompletions = 0;
        var cancellation = Task.Run(peer.Cancel, cancellationToken);
        Task<bool>? completion = null;
        Task<bool>? retirement = null;
        Task<RequestActivitySnapshot>? inspection = null;
        try
        {
            await callbackEntered.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            retirement = request.TryCancelAsync();
            completion = Task.Run(() =>
            {
                completingThread.TrySetResult(Thread.CurrentThread);
                return queued
                    ? request.CompleteQueuedCancellation(() => Interlocked.Increment(ref queuedCompletions))
                    : request.Complete(status, exception: null);
            }, cancellationToken);
            Thread thread = await completingThread.Task.WaitAsync(cancellationToken).ConfigureAwait(false);

            // Observe disposal waiting for the deliberately held peer cancellation callback.
            Assert.IsTrue(SpinWait.SpinUntil(
                () => (thread.ThreadState & ThreadState.WaitSleepJoin) != 0,
                TimeSpan.FromSeconds(10)),
                "Request completion did not reach peer cancellation-registration disposal.");
            inspection = Task.Run(request.GetSnapshot, cancellationToken);
            RequestActivitySnapshot activity = await inspection
                .WaitAsync(TimeSpan.FromSeconds(10), cancellationToken)
                .ConfigureAwait(false);
            Assert.AreEqual(correlationId, activity.CorrelationId);
            Assert.AreEqual("retirement-read", activity.Name);
            Assert.AreEqual(queued ? (long?)null : 23, activity.WorkspaceGeneration);
            Assert.AreEqual(status, activity.Status);
            Assert.IsTrue(activity.IsCancellationRequested);
            Assert.AreEqual(queued ? 1 : 0, Volatile.Read(ref queuedCompletions));
            Assert.IsFalse(cancellation.IsCompleted);
            Assert.IsFalse(completion.IsCompleted);
            Assert.IsFalse(retirement.IsCompleted);
            Assert.IsFalse(await request.TryCancelAsync().WaitAsync(cancellationToken).ConfigureAwait(false));
        }
        finally
        {
            // Release the callback before joining disposal, including when inspection fails.
            releaseCallback.Set();
            await cancellation.WaitAsync(TimeSpan.FromSeconds(10), CancellationToken.None).ConfigureAwait(false);
            if (completion is not null)
            {
                await completion.WaitAsync(TimeSpan.FromSeconds(10), CancellationToken.None).ConfigureAwait(false);
            }

            request.Complete(RequestExecutionStatus.Canceled, exception: null);
            if (retirement is not null)
            {
                await retirement.WaitAsync(TimeSpan.FromSeconds(10), CancellationToken.None).ConfigureAwait(false);
            }

            if (inspection is not null)
            {
                await inspection.WaitAsync(TimeSpan.FromSeconds(10), CancellationToken.None).ConfigureAwait(false);
            }
        }

        Assert.IsNotNull(completion);
        Assert.IsNotNull(retirement);
        Assert.IsTrue(await completion.ConfigureAwait(false));
        Assert.IsTrue(await retirement.ConfigureAwait(false));
        Assert.IsFalse(request.Complete(status, exception: null));
        Assert.IsFalse(request.CompleteQueuedCancellation(() => Interlocked.Increment(ref queuedCompletions)));
        Assert.AreEqual(queued ? 1 : 0, queuedCompletions);
        Assert.AreEqual(status, request.GetSnapshot().Status);
    }
}
