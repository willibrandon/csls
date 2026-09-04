using Csls.Debugger.Contracts;
using Csls.Debugger.Control;
using Csls.Debugger.Terminal;
using System.Runtime.CompilerServices;

namespace Csls.Debugger.Tests;

/// <summary>
/// Verifies terminal presentation ownership against real debugger control sessions.
/// </summary>
[TestClass]
[TestCategory("DebuggerTerminal")]
public sealed class DebuggerTerminalStateTests
{
    /// <summary>
    /// Gets or sets the current test's cancellation and diagnostic context.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// Accepts queued navigation for unchanged panes and rejects callbacks after their stop retires.
    /// </summary>
    [TestMethod]
    [Timeout(60000, CooperativeCancellation = true)]
    public async Task SelectionsRemainValidUntilTheirPaneMappingsRetire()
    {
        string repositoryRoot = DebuggerTestEnvironment.FindRepositoryRoot();
        string sourcePath = Path.Join(
            repositoryRoot,
            "tests",
            "Csls.TestProcessHost",
            "DebuggerFixture.cs");
        string[] sourceLines = await File.ReadAllLinesAsync(
            sourcePath,
            TestContext.CancellationToken).ConfigureAwait(false);
        int breakpointLine = sourceLines
            .Select(static (line, index) => (Line: line, Number: index + 1))
            .Single(static item => item.Line.Contains(
                "int localNumber = number + 1;",
                StringComparison.Ordinal))
            .Number;
        int nextStatementLine = sourceLines
            .Select(static (line, index) => (Line: line, Number: index + 1))
            .Single(static item => item.Line.Contains(
                "long localLong = number + 2L;",
                StringComparison.Ordinal))
            .Number;
        using var endpoint = DebuggerTerminalEndpoint.Create();
        DebuggerWorkerTestSession worker = await DebuggerWorkerTestSession
            .StartAsync(TestContext.CancellationToken).ConfigureAwait(false);
        await using ConfiguredAsyncDisposable workerCleanup = worker.ConfigureAwait(false);
        DebuggerRpcClient client = worker.Client;
        _ = await client.SetSourceBreakpointsAsync(
            new DebugSourceBreakpointSetRequest(
                sourcePath,
                [new DebugSourceBreakpointRequest(breakpointLine, null)]),
            TestContext.CancellationToken).ConfigureAwait(false);
        _ = await client.LaunchAsync(
            new DebugLaunchRequest
            {
                Program = Path.Join(
                    repositoryRoot,
                    "artifacts",
                    "bin",
                    "Csls.TestProcessHost",
                    "debug",
                    "csls-test-process-host.dll"),
                WorkingDirectory = endpoint.DirectoryPath,
                Arguments =
                [
                    "--debugger-fixture",
                    Path.Join(endpoint.DirectoryPath, "continue.signal")
                ],
                SourceFileMap = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["/_/"] = repositoryRoot
                }
            },
            TestContext.CancellationToken).ConfigureAwait(false);
        DebuggerTerminalState state = await DebuggerTerminalState.CreateAsync(
            client,
            TestContext.CancellationToken).ConfigureAwait(false);
        await using ConfiguredAsyncDisposable stateCleanup = state.ConfigureAwait(false);
        DebuggerTerminalViewSnapshot oldView = state.CaptureViewSnapshot();
        Assert.Contains("Stopped", oldView.Header);
        Assert.Contains("number = 42", string.Join('\n', oldView.VariableLines));
        Assert.IsGreaterThan(3, oldView.SourceLines.Length);
        Assert.IsGreaterThan(1, oldView.ThreadLines.Length);
        Assert.IsGreaterThan(1, oldView.StackLines.Length);
        await AssertUnavailableWatchValuesAsync(client, state.Snapshot, oldView.SelectedStackFrameIndex)
            .ConfigureAwait(false);
        await AssertBreakpointCursorAsync(state, oldView, client, nextStatementLine)
            .ConfigureAwait(false);
        await AssertQueuedSelectionsAsync(state, oldView).ConfigureAwait(false);

        DebuggerTerminalViewSnapshot beforeStep = state.CaptureViewSnapshot();
        await state.StepAsync(DebugStepKind.Over).ConfigureAwait(false);
        await state.PauseAsync().ConfigureAwait(false);
        DebuggerTerminalViewSnapshot currentView = state.CaptureViewSnapshot();
        Assert.AreEqual(DebugSessionState.Stopped, state.Snapshot.State);
        Assert.AreNotSame(beforeStep, currentView);
        Assert.IsGreaterThan(1, currentView.SourceLines.Length);
        Assert.IsGreaterThan(1, currentView.ThreadLines.Length);
        Assert.IsGreaterThan(1, currentView.StackLines.Length);
        int staleSourceIndex = (currentView.SourceFocusedIndex + 1) % currentView.SourceLines.Length;
        await state.SelectSourceLineAsync(staleSourceIndex, beforeStep)
            .ConfigureAwait(false);
        AssertViewUnchanged(state, currentView);
        int staleThreadIndex = (currentView.SelectedThreadIndex + 1) % currentView.ThreadLines.Length;
        await state.SelectThreadAsync(staleThreadIndex, beforeStep).ConfigureAwait(false);
        AssertViewUnchanged(state, currentView);
        int staleFrameIndex = (currentView.SelectedStackFrameIndex + 1) % currentView.StackLines.Length;
        await state.SelectStackFrameAsync(staleFrameIndex, beforeStep).ConfigureAwait(false);
        AssertViewUnchanged(state, currentView);

        await state.TerminateAsync().ConfigureAwait(false);
        Assert.AreEqual(DebugSessionState.Terminated, state.Snapshot.State);
    }

    private async Task AssertUnavailableWatchValuesAsync(
        DebuggerRpcClient client,
        DebugSessionSnapshot session,
        int frameIndex)
    {
        int threadId = session.StoppedThreadId.GetValueOrDefault();
        Assert.IsGreaterThan(0, threadId);
        DebugStackTrace stack = await client.GetStackAsync(
            new DebugStackRequest(threadId, 0, 200),
            TestContext.CancellationToken).ConfigureAwait(false);
        int frameId = stack.StackFrames[frameIndex].Id;
        var auxiliary = new DebuggerTerminalAuxiliaryState(client);
        await auxiliary.LoadAsync(session, TestContext.CancellationToken).ConfigureAwait(false);
        string moduleSummary = auxiliary.ModuleSummary;
        auxiliary.ClearWatches();
        auxiliary.ClearWatchValues("No managed frame is available.");
        Assert.AreEqual("No watches configured.", Assert.ContainsSingle(auxiliary.Lines));
        await auxiliary.AddWatchAsync(frameId, "number", TestContext.CancellationToken)
            .ConfigureAwait(false);
        Assert.AreEqual("number = 42  int", Assert.ContainsSingle(auxiliary.Lines));

        auxiliary.ClearWatchValues("No managed frame is available.");
        Assert.AreEqual("Watches", auxiliary.Title);
        Assert.AreEqual("No managed frame is available.", Assert.ContainsSingle(auxiliary.Lines));
        Assert.AreEqual(moduleSummary, auxiliary.ModuleSummary);

        await auxiliary.LoadWatchesAsync(frameId, TestContext.CancellationToken).ConfigureAwait(false);
        Assert.AreEqual("number = 42  int", Assert.ContainsSingle(auxiliary.Lines));
    }

    private async Task AssertBreakpointCursorAsync(
        DebuggerTerminalState state,
        DebuggerTerminalViewSnapshot view,
        DebuggerRpcClient client,
        int sourceLine)
    {
        DebugBreakpointSnapshot original = await client.GetBreakpointsAsync(
            TestContext.CancellationToken).ConfigureAwait(false);
        DebugSourceBreakpointInfo originalBreakpoint = Assert.ContainsSingle(original.SourceBreakpoints);
        int cursor = view.SourceLines
            .Select(static (line, index) => (Line: line, Index: index))
            .Single(static row => row.Line.EndsWith(
                "long localLong = number + 2L;",
                StringComparison.Ordinal))
            .Index;
        Assert.AreNotEqual(view.SourceFocusedIndex, cursor);
        await state.SelectSourceLineAsync(cursor, view).ConfigureAwait(false);
        await state.ToggleSourceBreakpointAsync().ConfigureAwait(false);
        DebuggerTerminalViewSnapshot marked = state.CaptureViewSnapshot();
        Assert.AreEqual(cursor, marked.SourceFocusedIndex);
        Assert.StartsWith("● ", marked.SourceLines[cursor]);
        DebugBreakpointSnapshot changed = await client.GetBreakpointsAsync(
            TestContext.CancellationToken).ConfigureAwait(false);
        Assert.HasCount(2, changed.SourceBreakpoints);
        DebugSourceBreakpointInfo added = Assert.ContainsSingle(
            changed.SourceBreakpoints.Where(breakpoint => breakpoint.Line == sourceLine));
        Assert.AreEqual(originalBreakpoint.SourcePath, added.SourcePath);
        Assert.IsTrue(added.Verified);

        await state.ToggleSourceBreakpointAsync().ConfigureAwait(false);
        DebuggerTerminalViewSnapshot restored = state.CaptureViewSnapshot();
        Assert.AreEqual(cursor, restored.SourceFocusedIndex);
        Assert.StartsWith("  ", restored.SourceLines[cursor]);
        DebugBreakpointSnapshot restoredBreakpoints = await client.GetBreakpointsAsync(
            TestContext.CancellationToken).ConfigureAwait(false);
        DebugSourceBreakpointInfo remaining = Assert.ContainsSingle(restoredBreakpoints.SourceBreakpoints);
        Assert.AreEqual(originalBreakpoint.SourcePath, remaining.SourcePath);
        Assert.AreEqual(originalBreakpoint.Line, remaining.Line);
        Assert.IsTrue(remaining.Verified);
        await state.SelectSourceLineAsync(view.SourceFocusedIndex, view).ConfigureAwait(false);
        Assert.AreEqual(view.SourceFocusedIndex, state.CaptureViewSnapshot().SourceFocusedIndex);
    }

    private static async Task AssertQueuedSelectionsAsync(
        DebuggerTerminalState state,
        DebuggerTerminalViewSnapshot view)
    {
        int firstSourceIndex = (view.SourceFocusedIndex + 1) % view.SourceLines.Length;
        int secondSourceIndex = (view.SourceFocusedIndex + 2) % view.SourceLines.Length;
        int thirdSourceIndex = (view.SourceFocusedIndex + 3) % view.SourceLines.Length;
        await state.SelectSourceLineAsync(firstSourceIndex, view).ConfigureAwait(false);
        Assert.AreEqual(firstSourceIndex, state.CaptureViewSnapshot().SourceFocusedIndex);
        await state.SelectSourceLineAsync(secondSourceIndex, view).ConfigureAwait(false);
        Assert.AreEqual(secondSourceIndex, state.CaptureViewSnapshot().SourceFocusedIndex);

        await state.CycleAuxiliaryPaneAsync().ConfigureAwait(false);
        Assert.AreEqual("Modules", state.CaptureViewSnapshot().AuxiliaryTitle);
        await state.SelectSourceLineAsync(thirdSourceIndex, view).ConfigureAwait(false);
        Assert.AreEqual(thirdSourceIndex, state.CaptureViewSnapshot().SourceFocusedIndex);
        await state.AddWatchAsync("number").ConfigureAwait(false);
        DebuggerTerminalViewSnapshot watchedView = state.CaptureViewSnapshot();
        Assert.Contains("Watching number.", watchedView.Header);
        Assert.Contains("number = 42", string.Join('\n', watchedView.AuxiliaryLines));
        await state.SelectSourceLineAsync(view.SourceFocusedIndex, view).ConfigureAwait(false);
        Assert.AreEqual(view.SourceFocusedIndex, state.CaptureViewSnapshot().SourceFocusedIndex);

        int otherFrameIndex = (view.SelectedStackFrameIndex + 1) % view.StackLines.Length;
        await state.SelectStackFrameAsync(otherFrameIndex, view).ConfigureAwait(false);
        DebuggerTerminalViewSnapshot otherFrameView = state.CaptureViewSnapshot();
        Assert.AreEqual(otherFrameIndex, otherFrameView.SelectedStackFrameIndex);
        Assert.IsGreaterThan(1, otherFrameView.SourceLines.Length);
        int staleSourceIndex =
            (otherFrameView.SourceFocusedIndex + 1) % otherFrameView.SourceLines.Length;
        await state.SelectSourceLineAsync(staleSourceIndex, view).ConfigureAwait(false);
        AssertViewUnchanged(state, otherFrameView);
        await state.SelectStackFrameAsync(view.SelectedStackFrameIndex, view).ConfigureAwait(false);
        Assert.AreEqual(view.SelectedStackFrameIndex, state.CaptureViewSnapshot().SelectedStackFrameIndex);
        Assert.Contains("number = 42", string.Join('\n', state.CaptureViewSnapshot().VariableLines));

        int otherThreadIndex = (view.SelectedThreadIndex + 1) % view.ThreadLines.Length;
        await state.SelectThreadAsync(otherThreadIndex, view).ConfigureAwait(false);
        Assert.AreEqual(otherThreadIndex, state.CaptureViewSnapshot().SelectedThreadIndex);
        await state.SelectThreadAsync(view.SelectedThreadIndex, view).ConfigureAwait(false);
        DebuggerTerminalViewSnapshot returnedView = state.CaptureViewSnapshot();
        Assert.AreEqual(view.SelectedThreadIndex, returnedView.SelectedThreadIndex);
        Assert.Contains("number = 42", string.Join('\n', returnedView.VariableLines));
        await state.SelectStackFrameAsync(otherFrameIndex, view).ConfigureAwait(false);
        AssertViewUnchanged(state, returnedView);
    }

    private static void AssertViewUnchanged(
        DebuggerTerminalState state,
        DebuggerTerminalViewSnapshot expected)
    {
        DebuggerTerminalViewSnapshot actual = state.CaptureViewSnapshot();
        Assert.AreSame(expected, actual);
        Assert.AreEqual(expected.Header, actual.Header);
        Assert.AreEqual(expected.SourceFocusedIndex, state.SourceFocusedIndex);
        Assert.AreEqual(expected.SelectedThreadIndex, state.SelectedThreadIndex);
        Assert.AreEqual(expected.SelectedStackFrameIndex, state.SelectedStackFrameIndex);
        Assert.AreEqual(
            string.Join('\n', expected.SourceLines),
            string.Join('\n', state.SourceLines));
        Assert.AreEqual(
            string.Join('\n', expected.ThreadLines),
            string.Join('\n', state.ThreadLines));
        Assert.AreEqual(
            string.Join('\n', expected.StackLines),
            string.Join('\n', state.StackLines));
        Assert.AreEqual(
            string.Join('\n', expected.VariableLines),
            string.Join('\n', state.VariableLines));
    }
}
