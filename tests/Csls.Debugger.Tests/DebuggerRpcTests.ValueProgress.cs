using Csls.Debugger.Contracts;
using Csls.Debugger.Control;
using System.Runtime.CompilerServices;

namespace Csls.Debugger.Tests;

/// <summary>
/// Verifies live-value progress marshaling over a real debugger worker connection.
/// </summary>
public sealed partial class DebuggerRpcTests
{
    private static readonly int[] s_liveValuePageCheckpoints = [256, 512, 768, 1000];

    /// <summary>
    /// Receives ordered request-scoped progress and inspects published children over private RPC.
    /// </summary>
    [TestMethod]
    [Timeout(30000, CooperativeCancellation = true)]
    public async Task PrivateRpcReportsLiveValueProgress()
    {
        string root = FindRepositoryRoot();
        string source = Path.Join(root, "tests", "Csls.TestProcessHost", "DebuggerDumpArrayFixture.cs");
        string[] lines = await File.ReadAllLinesAsync(source, TestContext.CancellationToken).ConfigureAwait(false);
        int line = Array.FindIndex(lines, static text => text.Contains("DebuggerBlockingWait.Wait(announcement);", StringComparison.Ordinal)) + 1;
        Assert.IsGreaterThan(0, line);
        DebuggerWorkerTestSession worker = await DebuggerWorkerTestSession.StartAsync(TestContext.CancellationToken).ConfigureAwait(false);
        await using ConfiguredAsyncDisposable cleanup = worker.ConfigureAwait(false);
        DebuggerRpcClient client = worker.Client;
        _ = await client.SetSourceBreakpointsAsync(new DebugSourceBreakpointSetRequest(source, [new(line, null)]),
            TestContext.CancellationToken).ConfigureAwait(false);
        _ = await client.LaunchAsync(new DebugLaunchRequest
        {
            Program = ResolveTestProcessHost(root),
            WorkingDirectory = root,
            Arguments = ["--debugger-dump-arrays", nameof(PrivateRpcReportsLiveValueProgress)],
            SourceFileMap = CreateDefaultSourceFileMap()
        }, TestContext.CancellationToken).ConfigureAwait(false);
        DebugSessionSnapshot stopped = await WaitForStoppedAsync(client, TestContext.CancellationToken).ConfigureAwait(false);
        DebugStackTrace stack = await client.GetStackAsync(new DebugStackRequest(
            stopped.StoppedThreadId ?? throw new InvalidOperationException("No stopped thread."), 0, 1),
            TestContext.CancellationToken).ConfigureAwait(false);
        DebugEvaluateResult array = await client.EvaluateAsync(new DebugEvaluateRequest(stack.StackFrames[0].Id, "many"),
            TestContext.CancellationToken).ConfigureAwait(false);
        var progress = new ValueReadProgressRecorder();
        IReadOnlyList<DebugVariableInfo> page = await client.GetVariablesAsync(new DebugVariablesRequest(
            array.VariablesReference, 100, 1000, AllowTargetCodeExecution: false, DebugVariableFilter.Indexed)
        { Progress = progress },
            TestContext.CancellationToken).ConfigureAwait(false);
        DebugValueReadProgress terminal = await progress.Terminal.WaitAsync(TestContext.CancellationToken).ConfigureAwait(false);
        Assert.HasCount(1000, page);
        Assert.AreEqual("[100]", page[0].Name);
        Assert.AreEqual("[1099]", page[^1].Name);
        Assert.AreEqual(DebugValueReadState.Completed, terminal.State);
        Assert.AreEqual(array.VariablesReference, terminal.VariablesReference);
        Assert.AreEqual(1000, terminal.FormattedValues);
        Assert.AreEqual(1000, terminal.PageValues);
        DebugValueReadProgress[] updates = progress.Updates;
        Assert.AreSequenceEqual(s_liveValuePageCheckpoints, updates.Select(static value => value.FormattedValues));
        foreach (DebugValueReadProgress update in updates[..^1])
        {
            Assert.AreEqual(DebugValueReadState.Reading, update.State);
            Assert.AreEqual(0, update.PageValues);
            Assert.AreEqual(array.VariablesReference, update.VariablesReference);
        }

        var childProgress = new ValueReadProgressRecorder();
        IReadOnlyList<DebugVariableInfo> child = await client.GetVariablesAsync(new DebugVariablesRequest(
            page[0].VariablesReference, 0, 1, AllowTargetCodeExecution: false)
        { Progress = childProgress },
            TestContext.CancellationToken).ConfigureAwait(false);
        DebugValueReadProgress childTerminal = await childProgress.Terminal.WaitAsync(TestContext.CancellationToken).ConfigureAwait(false);
        Assert.AreEqual("41", Assert.ContainsSingle(child).Value);
        Assert.AreEqual(page[0].VariablesReference, childTerminal.VariablesReference);
        Assert.AreEqual(1, childTerminal.FormattedValues);
        Assert.AreEqual(1, childTerminal.PageValues);
        Assert.AreEqual(DebugValueReadState.Completed, childTerminal.State);
        Assert.HasCount(4, progress.Updates, "Completed requests must retain their own progress stream.");
        DebugSessionSnapshot unchanged = await client.GetSessionAsync(TestContext.CancellationToken).ConfigureAwait(false);
        Assert.AreEqual(stopped.StopGeneration, unchanged.StopGeneration);
        Assert.AreEqual(DebugSessionState.Stopped, unchanged.State);
        DebugSessionSnapshot terminated = await client.TerminateAsync(TestContext.CancellationToken).ConfigureAwait(false);
        Assert.AreEqual(DebugSessionState.Terminated, terminated.State);
    }
}
