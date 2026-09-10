using Csls.Debugger.Contracts;
using Csls.Debugger.Control;
using System.Runtime.CompilerServices;

namespace Csls.Debugger.Tests;

/// <summary>
/// Verifies request-scoped stack progress over a real debugger worker transport.
/// </summary>
public sealed partial class DebuggerRpcTests
{
    /// <summary>
    /// Receives bounded native traversal updates and terminal ownership through private RPC.
    /// </summary>
    [TestMethod]
    [Timeout(30000, CooperativeCancellation = true)]
    public async Task PrivateRpcReportsStackProgress()
    {
        string repository = FindRepositoryRoot();
        string source = Path.Join(repository, "tests", "Csls.TestProcessHost", "DebuggerDeepStackFixture.cs");
        string[] lines = await File.ReadAllLinesAsync(source, TestContext.CancellationToken).ConfigureAwait(false);
        int line = Array.FindIndex(lines, static text => text.Contains("return CompleteDescent(entered);", StringComparison.Ordinal)) + 1;
        Assert.IsGreaterThan(0, line);
        DebuggerWorkerTestSession worker = await DebuggerWorkerTestSession.StartAsync(TestContext.CancellationToken).ConfigureAwait(false);
        await using ConfiguredAsyncDisposable cleanup = worker.ConfigureAwait(false);
        DebuggerRpcClient client = worker.Client;
        _ = await client.SetSourceBreakpointsAsync(new DebugSourceBreakpointSetRequest(source, [new(line, null)]),
            TestContext.CancellationToken).ConfigureAwait(false);
        _ = await client.LaunchAsync(new DebugLaunchRequest
        {
            Program = ResolveTestProcessHost(repository),
            WorkingDirectory = repository,
            Arguments = ["--debugger-deep-stack-fixture", "5000"],
            SourceFileMap = CreateDefaultSourceFileMap()
        }, TestContext.CancellationToken).ConfigureAwait(false);
        DebugSessionSnapshot stopped = await WaitForStoppedAsync(client, TestContext.CancellationToken).ConfigureAwait(false);
        int threadId = stopped.StoppedThreadId ?? throw new InvalidOperationException("No stopped thread.");
        var progress = new StackWalkProgressRecorder();
        DebugStackTrace stack = await client.GetStackAsync(new DebugStackRequest(threadId, 4096, 2) { Progress = progress },
            TestContext.CancellationToken).ConfigureAwait(false);
        DebugStackWalkProgress terminal = await progress.Terminal.WaitAsync(TestContext.CancellationToken).ConfigureAwait(false);
        Assert.HasCount(2, stack.StackFrames);
        Assert.IsNull(stack.TotalFrames);
        Assert.AreEqual(DebugStackWalkState.Completed, terminal.State);
        Assert.AreEqual(threadId, terminal.ThreadId);
        Assert.AreEqual(4098, terminal.InspectedFrames);
        Assert.AreEqual(2, terminal.CapturedFrames);
        Assert.AreEqual(2, terminal.RetainedFrameBindings);
        Assert.AreEqual(0, terminal.OwnedWalkInterfaces);
        DebugStackWalkProgress[] updates = progress.Updates;
        Assert.HasCount(17, updates);
        for (int index = 0; index < updates.Length - 1; index++)
        {
            Assert.AreEqual((index + 1) * 256, updates[index].InspectedFrames);
            Assert.AreEqual(DebugStackWalkState.Walking, updates[index].State);
            Assert.AreEqual(0, updates[index].CapturedFrames);
            Assert.AreEqual(0, updates[index].RetainedFrameBindings);
            Assert.AreEqual(3, updates[index].OwnedWalkInterfaces);
        }

        var secondProgress = new StackWalkProgressRecorder();
        DebugStackTrace refreshed = await client.GetStackAsync(new DebugStackRequest(threadId, 4097, 1) { Progress = secondProgress },
            TestContext.CancellationToken).ConfigureAwait(false);
        DebugStackWalkProgress secondTerminal = await secondProgress.Terminal.WaitAsync(TestContext.CancellationToken).ConfigureAwait(false);
        Assert.AreEqual(stack.StackFrames[1].Id, refreshed.StackFrames[0].Id);
        Assert.AreEqual(1, secondTerminal.CapturedFrames);
        Assert.AreEqual(2, secondTerminal.RetainedFrameBindings);
        Assert.HasCount(17, progress.Updates, "A completed request must not receive another request's progress.");
        DebugSessionSnapshot terminated = await client.TerminateAsync(TestContext.CancellationToken).ConfigureAwait(false);
        Assert.AreEqual(DebugSessionState.Terminated, terminated.State);
    }
}
