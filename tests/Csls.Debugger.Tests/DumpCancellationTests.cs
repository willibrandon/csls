using Csls.Debugger.Contracts;
using Csls.Debugger.Control;
using Csls.Debugger.Dump;
using Microsoft.Diagnostics.NETCore.Client;
using System.Runtime.CompilerServices;

namespace Csls.Debugger.Tests;

/// <summary>
/// Verifies cancellation inside actual offline CoreCLR reads and subsequent immutable-session recovery.
/// </summary>
[TestClass]
public sealed class DumpCancellationTests : DapTestContext
{
    /// <summary>
    /// Cancels from actual captured-memory progress and preserves the same logical frame and generation.
    /// </summary>
    /// <param name="includeHeap">Whether the captured process includes managed heap pages.</param>
    /// <param name="checkpoint">The real callback checkpoint selected by the client.</param>
    [TestMethod]
    [DataRow(false, 1L)]
    [DataRow(true, 1L)]
    [DataRow(false, 64L)]
    [DataRow(true, 64L)]
    [Timeout(60000, CooperativeCancellation = true)]
    public async Task CapturedReadCancellationPreservesSession(bool includeHeap, long checkpoint)
    {
        DebuggerDumpFixture fixture = await DebuggerDumpFixture.CreateAsync(ResolveTestProcessHost(),
            TestContext.CancellationToken, captureFrameValues: true, includeHeap).ConfigureAwait(false);
        await using ConfiguredAsyncDisposable fixtureCleanup = fixture.ConfigureAwait(false);
        var service = new DumpDebuggerControlService();
        await using ConfiguredAsyncDisposable serviceCleanup = service.ConfigureAwait(false);
        DebugScopeInfo locals = await OpenFrameAsync(service, fixture.DumpPath).ConfigureAwait(false);
        DebugSessionSnapshot initial = await service.GetSessionAsync(TestContext.CancellationToken).ConfigureAwait(false);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.CancellationToken);
        var observer = new DumpReadProgressRecorder(cancellation, checkpoint);
        OperationCanceledException canceled = await Assert.ThrowsExactlyAsync<OperationCanceledException>(() => service.GetVariablesAsync(
            new DebugVariablesRequest(locals.VariablesReference, 0, 2, false) { DumpReadProgress = observer },
            cancellation.Token)).ConfigureAwait(false);
        Assert.AreEqual(cancellation.Token, canceled.CancellationToken);
        DebugDumpReadProgress[] updates = observer.Updates;
        Assert.IsGreaterThan(1, updates.Length);
        Assert.AreEqual(DebugDumpReadState.Canceled, updates[^1].State);
        Assert.AreEqual(checkpoint, updates[^1].MemoryReads + updates[^1].ContextReads);
        Assert.IsGreaterThan(0L, updates[^1].BytesRead);
        AssertMonotonicProgress(updates);
        IReadOnlyList<DebugVariableInfo> recovered = await service.GetVariablesAsync(
            new DebugVariablesRequest(locals.VariablesReference, 0, 2, false), TestContext.CancellationToken).ConfigureAwait(false);
        AssertLocals(recovered, fixture.CaptureType);
        Assert.AreEqual(initial, await service.GetSessionAsync(TestContext.CancellationToken).ConfigureAwait(false));
        _ = await service.TerminateAsync(TestContext.CancellationToken).ConfigureAwait(false);
        using FileStream released = File.Open(fixture.DumpPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        Assert.IsGreaterThan(0L, released.Length);
    }

    /// <summary>
    /// Preserves client observer failures as failures and allows subsequent inspection of the same frame.
    /// </summary>
    /// <param name="failureKind">The recoverable or unexpected exception raised by the observer.</param>
    [TestMethod]
    [DataRow("io")]
    [DataRow("cancellation")]
    [DataRow("unexpected")]
    [DataRow("derived")]
    [Timeout(60000, CooperativeCancellation = true)]
    public async Task CapturedReadProgressFailurePreservesSession(string failureKind)
    {
        DebuggerDumpFixture fixture = await DebuggerDumpFixture.CreateAsync(ResolveTestProcessHost(),
            TestContext.CancellationToken, captureFrameValues: true).ConfigureAwait(false);
        await using ConfiguredAsyncDisposable fixtureCleanup = fixture.ConfigureAwait(false);
        var service = new DumpDebuggerControlService();
        await using ConfiguredAsyncDisposable serviceCleanup = service.ConfigureAwait(false);
        DebugScopeInfo locals = await OpenFrameAsync(service, fixture.DumpPath).ConfigureAwait(false);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.CancellationToken);
        Exception failure = failureKind switch
        {
            "cancellation" => new OperationCanceledException("Observer stopped."),
            "unexpected" => new FormatException("Observer rejected the notification."),
            "derived" => new EndOfStreamException("Observer reached the end of its output."),
            _ => new IOException("Observer closed.")
        };
        var observer = new DumpReadProgressRecorder(cancellation, failure: failure);
        var request = new DebugVariablesRequest(locals.VariablesReference, 0, 2, false) { DumpReadProgress = observer };
        if (failureKind == "unexpected")
        {
            FormatException rejected = await Assert.ThrowsExactlyAsync<FormatException>(() => service.GetVariablesAsync(
                request, cancellation.Token)).ConfigureAwait(false);
            Assert.AreSame(failure, rejected);
            Assert.Contains("DumpReadProgressRecorder.Report", rejected.StackTrace!);
        }
        else
        {
            InvalidOperationException rejected = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => service.GetVariablesAsync(
                request, cancellation.Token)).ConfigureAwait(false);
            Assert.AreSame(failure, rejected.InnerException);
        }
        Assert.IsFalse(cancellation.IsCancellationRequested);
        Assert.AreEqual(DebugDumpReadState.Reading, Assert.ContainsSingle(observer.Updates).State);
        AssertLocals(await service.GetVariablesAsync(new DebugVariablesRequest(locals.VariablesReference, 0, 2, false),
            TestContext.CancellationToken).ConfigureAwait(false), fixture.CaptureType);
        _ = await service.TerminateAsync(TestContext.CancellationToken).ConfigureAwait(false);
        using FileStream released = File.Open(fixture.DumpPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        Assert.IsGreaterThan(0L, released.Length);
    }

    /// <summary>
    /// Retains caller cancellation and a failed terminal notification as independently inspectable failures.
    /// </summary>
    [TestMethod]
    [Timeout(60000, CooperativeCancellation = true)]
    public async Task CapturedReadCancellationRetainsTerminalObserverFailure()
    {
        DebuggerDumpFixture fixture = await DebuggerDumpFixture.CreateAsync(ResolveTestProcessHost(),
            TestContext.CancellationToken, captureFrameValues: true).ConfigureAwait(false);
        await using ConfiguredAsyncDisposable fixtureCleanup = fixture.ConfigureAwait(false);
        var service = new DumpDebuggerControlService();
        await using ConfiguredAsyncDisposable serviceCleanup = service.ConfigureAwait(false);
        DebugScopeInfo locals = await OpenFrameAsync(service, fixture.DumpPath).ConfigureAwait(false);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.CancellationToken);
        var failure = new IOException("Terminal notification closed.");
        var observer = new DumpReadProgressRecorder(cancellation, checkpoint: 1, failure: failure,
            failureState: DebugDumpReadState.Canceled);
        AggregateException rejected = await Assert.ThrowsExactlyAsync<AggregateException>(() => service.GetVariablesAsync(
            new DebugVariablesRequest(locals.VariablesReference, 0, 2, false) { DumpReadProgress = observer },
            cancellation.Token)).ConfigureAwait(false);
        Assert.HasCount(2, rejected.InnerExceptions);
        OperationCanceledException canceled = Assert.IsInstanceOfType<OperationCanceledException>(rejected.InnerExceptions[0]);
        Assert.AreEqual(cancellation.Token, canceled.CancellationToken);
        Assert.AreSame(failure, rejected.InnerExceptions[1].InnerException);
        Assert.HasCount(2, observer.Updates);
        Assert.AreEqual(DebugDumpReadState.Canceled, observer.Updates[^1].State);
        AssertLocals(await service.GetVariablesAsync(new DebugVariablesRequest(locals.VariablesReference, 0, 2, false),
            TestContext.CancellationToken).ConfigureAwait(false), fixture.CaptureType);
    }

    /// <summary>
    /// Rejects a request canceled before it starts without activating native reads or publishing progress.
    /// </summary>
    [TestMethod]
    [Timeout(60000, CooperativeCancellation = true)]
    public async Task PreCanceledCapturedReadPreservesSession()
    {
        DebuggerDumpFixture fixture = await DebuggerDumpFixture.CreateAsync(ResolveTestProcessHost(),
            TestContext.CancellationToken, captureFrameValues: true).ConfigureAwait(false);
        await using ConfiguredAsyncDisposable fixtureCleanup = fixture.ConfigureAwait(false);
        var service = new DumpDebuggerControlService();
        await using ConfiguredAsyncDisposable serviceCleanup = service.ConfigureAwait(false);
        DebugScopeInfo locals = await OpenFrameAsync(service, fixture.DumpPath).ConfigureAwait(false);
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync().ConfigureAwait(false);
        var observer = new DumpReadProgressRecorder(cancellation);
        OperationCanceledException canceled = await Assert.ThrowsAsync<OperationCanceledException>(() => service.GetVariablesAsync(
            new DebugVariablesRequest(locals.VariablesReference, 0, 2, false) { DumpReadProgress = observer },
            cancellation.Token)).ConfigureAwait(false);
        Assert.AreEqual(cancellation.Token, canceled.CancellationToken);
        Assert.IsEmpty(observer.Updates);
        AssertLocals(await service.GetVariablesAsync(new DebugVariablesRequest(locals.VariablesReference, 0, 2, false),
            TestContext.CancellationToken).ConfigureAwait(false), fixture.CaptureType);
    }

    /// <summary>
    /// Preserves a terminal observer failure after native references are released and permits another read.
    /// </summary>
    [TestMethod]
    [Timeout(60000, CooperativeCancellation = true)]
    public async Task CapturedReadCompletionFailurePreservesSession()
    {
        DebuggerDumpFixture fixture = await DebuggerDumpFixture.CreateAsync(ResolveTestProcessHost(),
            TestContext.CancellationToken, captureFrameValues: true).ConfigureAwait(false);
        await using ConfiguredAsyncDisposable fixtureCleanup = fixture.ConfigureAwait(false);
        var service = new DumpDebuggerControlService();
        await using ConfiguredAsyncDisposable serviceCleanup = service.ConfigureAwait(false);
        DebugScopeInfo locals = await OpenFrameAsync(service, fixture.DumpPath).ConfigureAwait(false);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.CancellationToken);
        var failure = new IOException("Completion observer closed.");
        var observer = new DumpReadProgressRecorder(cancellation, failure: failure,
            failureState: DebugDumpReadState.Completed);
        InvalidOperationException rejected = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => service.GetVariablesAsync(
            new DebugVariablesRequest(locals.VariablesReference, 0, 2, false) { DumpReadProgress = observer },
            cancellation.Token)).ConfigureAwait(false);
        Assert.AreSame(failure, rejected.InnerException);
        Assert.IsFalse(cancellation.IsCancellationRequested);
        Assert.AreEqual(DebugDumpReadState.Completed, observer.Updates[^1].State);
        AssertMonotonicProgress(observer.Updates);
        AssertLocals(await service.GetVariablesAsync(new DebugVariablesRequest(locals.VariablesReference, 0, 2, false),
            TestContext.CancellationToken).ConfigureAwait(false), fixture.CaptureType);
    }

    /// <summary>
    /// Reports bounded real read work, empty pages, and successful results when cancellation arrives after completion.
    /// </summary>
    /// <param name="empty">Whether to request a page beyond the captured local slots.</param>
    /// <param name="cancelCompleted">Whether the client cancels after the completed notification.</param>
    [TestMethod]
    [DataRow(false, false)]
    [DataRow(true, false)]
    [DataRow(false, true)]
    [Timeout(60000, CooperativeCancellation = true)]
    public async Task CapturedReadProgressReportsBoundedWork(bool empty, bool cancelCompleted)
    {
        DebuggerDumpFixture fixture = await DebuggerDumpFixture.CreateAsync(ResolveTestProcessHost(),
            TestContext.CancellationToken, captureFrameValues: true).ConfigureAwait(false);
        await using ConfiguredAsyncDisposable fixtureCleanup = fixture.ConfigureAwait(false);
        var service = new DumpDebuggerControlService();
        await using ConfiguredAsyncDisposable serviceCleanup = service.ConfigureAwait(false);
        DebugScopeInfo locals = await OpenFrameAsync(service, fixture.DumpPath).ConfigureAwait(false);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.CancellationToken);
        var observer = new DumpReadProgressRecorder(cancellation, cancelCompleted: cancelCompleted);
        IReadOnlyList<DebugVariableInfo> values = await service.GetVariablesAsync(
            new DebugVariablesRequest(locals.VariablesReference, empty ? int.MaxValue : 0, 2, false)
            { DumpReadProgress = observer }, cancellation.Token).ConfigureAwait(false);
        if (empty)
        {
            Assert.IsEmpty(values);
        }
        else
        {
            AssertLocals(values, fixture.CaptureType);
        }

        Assert.AreEqual(cancelCompleted, cancellation.IsCancellationRequested);
        DebugDumpReadProgress[] updates = observer.Updates;
        DebugDumpReadProgress completed = updates[^1];
        Assert.AreEqual(DebugDumpReadState.Completed, completed.State);
        Assert.IsGreaterThan(0L, completed.MemoryReads);
        Assert.IsGreaterThan(0L, completed.BytesRead);
        AssertMonotonicProgress(updates);
    }

    /// <summary>
    /// Carries captured-memory progress through the real supervised dump worker's private RPC transport.
    /// </summary>
    [TestMethod]
    [Timeout(60000, CooperativeCancellation = true)]
    public async Task PrivateRpcReportsCapturedReadProgress()
    {
        DebuggerDumpFixture fixture = await DebuggerDumpFixture.CreateAsync(ResolveTestProcessHost(),
            TestContext.CancellationToken, captureFrameValues: true).ConfigureAwait(false);
        await using ConfiguredAsyncDisposable fixtureCleanup = fixture.ConfigureAwait(false);
        string workerPath = Path.Join(FindRepositoryRoot(), "artifacts", "bin", "Csls.Debugger.Dump.Worker", "debug",
            "csls-debugger-dump-worker.dll");
        DebuggerWorkerProcess worker = await DebuggerWorkerProcess.StartAsync(workerPath, false,
            TestContext.CancellationToken).ConfigureAwait(false);
        await using ConfiguredAsyncDisposable workerCleanup = worker.ConfigureAwait(false);
        _ = await worker.Client.OpenDumpAsync(fixture.OpenRequest,
            TestContext.CancellationToken).ConfigureAwait(false);
        DebugScopeInfo locals = await SelectFrameAsync(worker.Client).ConfigureAwait(false);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.CancellationToken);
        var observer = new DumpReadProgressRecorder(cancellation);
        IReadOnlyList<DebugVariableInfo> values = await worker.Client.GetVariablesAsync(
            new DebugVariablesRequest(locals.VariablesReference, 0, 2, false) { DumpReadProgress = observer },
            TestContext.CancellationToken).ConfigureAwait(false);
        AssertLocals(values, fixture.CaptureType);
        DebugDumpReadProgress terminal = await observer.Terminal.WaitAsync(TestContext.CancellationToken).ConfigureAwait(false);
        Assert.AreEqual(DebugDumpReadState.Completed, terminal.State);
        Assert.IsGreaterThan(0L, terminal.MemoryReads);
        AssertMonotonicProgress(observer.Updates);
    }

    private async Task<DebugScopeInfo> OpenFrameAsync(DumpDebuggerControlService service, string dumpPath)
    {
        _ = await service.OpenDumpAsync(new DebugDumpOpenRequest(dumpPath, BinarySearchPaths:
            [Path.GetDirectoryName(ResolveTestProcessHost()) ?? throw new InvalidOperationException("The fixture has no directory.")]),
            TestContext.CancellationToken).ConfigureAwait(false);
        return await SelectFrameAsync(service).ConfigureAwait(false);
    }

    private async Task<DebugScopeInfo> SelectFrameAsync(IDebuggerInspectionTarget service)
    {
        IReadOnlyList<DebugThreadInfo> threads = await service.GetThreadsAsync(TestContext.CancellationToken).ConfigureAwait(false);
        List<DebugStackFrameInfo> frames = [];
        foreach (DebugThreadInfo thread in threads)
        {
            DebugStackTrace stack = await service.GetStackAsync(new DebugStackRequest(thread.Id, 0, 0),
                TestContext.CancellationToken).ConfigureAwait(false);
            frames.AddRange(stack.StackFrames);
        }

        DebugStackFrameInfo selected = Assert.ContainsSingle(frames.Where(frame =>
            frame.Name.Contains("DebuggerFixture.WaitForSignal", StringComparison.Ordinal)));
        IReadOnlyList<DebugScopeInfo> scopes = await service.GetScopesAsync(new DebugScopesRequest(selected.Id),
            TestContext.CancellationToken).ConfigureAwait(false);
        return Assert.ContainsSingle(scopes.Where(scope => scope.Name == "Locals"));
    }

    private static void AssertLocals(IReadOnlyList<DebugVariableInfo> values, DumpType captureType)
    {
        Assert.HasCount(2, values);
        Assert.AreEqual("localNumber", values[0].Name);
        Assert.AreEqual("localLong", values[1].Name);
        if (OperatingSystem.IsWindows() && captureType == DumpType.Triage)
        {
            foreach (DebugVariableInfo value in values)
            {
                Assert.AreEqual(DebugVariablePresentationKind.Unavailable, value.PresentationKind);
                Assert.AreEqual("Captured value unavailable: storage was filtered when the dump was created.", value.Value);
                Assert.AreEqual(0, value.VariablesReference);
                Assert.IsNull(value.MemoryReference);
            }
            return;
        }
        Assert.AreEqual("43", values[0].Value);
        Assert.AreEqual("int", values[0].Type);
        Assert.AreEqual("44", values[1].Value);
        Assert.AreEqual("long", values[1].Type);
    }

    private static void AssertMonotonicProgress(DebugDumpReadProgress[] updates)
    {
        Assert.IsGreaterThan(1, updates.Length);
        long reads = 0;
        long bytes = 0;
        for (int index = 0; index < updates.Length; index++)
        {
            DebugDumpReadProgress update = updates[index];
            Assert.IsGreaterThanOrEqualTo(reads, update.MemoryReads + update.ContextReads);
            Assert.IsGreaterThanOrEqualTo(bytes, update.BytesRead);
            if (index < updates.Length - 1)
            {
                Assert.AreEqual(DebugDumpReadState.Reading, update.State);
                Assert.AreEqual(index == 0 ? 1L : index * 64L, update.MemoryReads + update.ContextReads);
            }
            reads = update.MemoryReads + update.ContextReads;
            bytes = update.BytesRead;
        }
    }
}
