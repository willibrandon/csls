using Csls.Debugger.Contracts;
using Csls.Debugger.Control;
using System.Runtime.CompilerServices;

namespace Csls.Debugger.Tests;

/// <summary>
/// Verifies the private debugger control protocol over its real local transport.
/// </summary>
[TestClass]
public sealed partial class DebuggerRpcTests
{
    /// <summary>
    /// Gets the active MSTest context and its framework-managed cancellation token.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// Launches, stops, and inspects a real managed process through a Unix socket.
    /// </summary>
    [TestMethod]
    [Timeout(30000, CooperativeCancellation = true)]
    [OSCondition(ConditionMode.Exclude, OperatingSystems.Windows)]
    public async Task PrivateRpcInspectsStoppedManagedTarget()
    {
        string testDirectory = Path.Join(
            Path.GetTempPath(),
            $"csls-debugger-rpc-{Guid.NewGuid():N}");
        Directory.CreateDirectory(testDirectory);
        try
        {
            await ExerciseRpcAsync(testDirectory, TestContext.CancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            await DebuggerTestDirectoryReleaseWaiter.DeleteAsync(
                testDirectory,
                TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        }
    }

    private static async Task ExerciseRpcAsync(
        string testDirectory,
        CancellationToken cancellationToken)
    {
        string repositoryRoot = FindRepositoryRoot();
        string sourcePath = Path.Join(
            repositoryRoot,
            "tests",
            "Csls.TestProcessHost",
            "DebuggerFixture.cs");
        string[] sourceLines = await File.ReadAllLinesAsync(sourcePath, cancellationToken)
            .ConfigureAwait(false);
        int breakpointLine = sourceLines
            .Select(static (line, index) => (Line: line, Number: index + 1))
            .Single(static candidate => candidate.Line.Contains(
                "Thread.Sleep(1);",
                StringComparison.Ordinal))
            .Number;
        int localLine = sourceLines
            .Select(static (line, index) => (Line: line, Number: index + 1))
            .Single(static candidate => candidate.Line.Contains(
                "int localNumber = number + 1;",
                StringComparison.Ordinal))
            .Number;
        string signalPath = Path.Join(testDirectory, "continue.signal");

        DebuggerWorkerTestSession worker = await DebuggerWorkerTestSession
            .StartAsync(cancellationToken).ConfigureAwait(false);
        await using ConfiguredAsyncDisposable workerDisposal = worker.ConfigureAwait(false);
        DebuggerRpcClient client = worker.Client;
        var resourcesChanged = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        client.ResourceChanged += (_, change) =>
        {
            if (change.Kind.HasFlag(DebuggerResourceChangeKind.Session))
            {
                resourcesChanged.TrySetResult(true);
            }
        };

        DebugSessionSnapshot created = await client.GetSessionAsync(cancellationToken)
            .ConfigureAwait(false);
        Assert.AreEqual(DebugSessionState.Created, created.State);
        IReadOnlyList<DebugSourceBreakpointInfo> pending = await client
            .SetSourceBreakpointsAsync(
                new DebugSourceBreakpointSetRequest(
                    sourcePath,
                    [new DebugSourceBreakpointRequest(breakpointLine, null)]),
                cancellationToken)
            .ConfigureAwait(false);
        Assert.HasCount(1, pending);
        Assert.IsFalse(pending[0].Verified);

        _ = await client.LaunchAsync(
            new DebugLaunchRequest
            {
                Program = ResolveTestProcessHost(repositoryRoot),
                WorkingDirectory = repositoryRoot,
                Arguments = ["--debugger-fixture", signalPath],
                SourceFileMap = CreateDefaultSourceFileMap()
            },
            cancellationToken).ConfigureAwait(false);
        DebugSessionSnapshot stopped = await WaitForStoppedAsync(client, cancellationToken)
            .ConfigureAwait(false);
        await resourcesChanged.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        Assert.AreEqual("breakpoint", stopped.StopReason);
        Assert.IsNotNull(stopped.StoppedThreadId);
        int stoppedThreadId = stopped.StoppedThreadId
            ?? throw new InvalidOperationException("The target did not report a stopped thread.");
        Assert.IsGreaterThan(0, stoppedThreadId);

        IReadOnlyList<DebugThreadInfo> threads = await client.GetThreadsAsync(cancellationToken)
            .ConfigureAwait(false);
        Assert.IsNotEmpty(threads);
        DebugStackTrace stack = await client.GetStackAsync(
            new DebugStackRequest(stoppedThreadId, 0, 64),
            cancellationToken).ConfigureAwait(false);
        DebugStackFrameInfo frame = stack.StackFrames.Single(candidate =>
            DebuggerTestPath.AreEquivalent(candidate.Source?.Path, sourcePath) &&
            candidate.Line == breakpointLine);
        IReadOnlyList<DebugScopeInfo> scopes = await client.GetScopesAsync(
            new DebugScopesRequest(frame.Id),
            cancellationToken).ConfigureAwait(false);
        Assert.HasCount(2, scopes);
        DebugScopeInfo arguments = scopes.Single(static scope => scope.Name == "Arguments");
        IReadOnlyList<DebugVariableInfo> variables = await client.GetVariablesAsync(
            new DebugVariablesRequest(
                arguments.VariablesReference,
                0,
                0,
                AllowTargetCodeExecution: false),
            cancellationToken).ConfigureAwait(false);
        DebugVariableInfo number = variables.Single(static variable => variable.Name == "number");
        Assert.AreEqual("42", number.Value);
        Assert.AreEqual("int", number.Type);
        DebugScopeInfo locals = scopes.Single(static scope => scope.Name == "Locals");
        IReadOnlyList<DebugVariableInfo> localVariables = await client.GetVariablesAsync(
            new DebugVariablesRequest(
                locals.VariablesReference,
                0,
                0,
                AllowTargetCodeExecution: false),
            cancellationToken).ConfigureAwait(false);
        DebugVariableInfo localNumber = localVariables.Single(
            static variable => variable.Name == "localNumber");
        Assert.AreEqual("43", localNumber.Value);
        Assert.AreEqual("int", localNumber.Type);
        Assert.AreEqual("localNumber", localNumber.EvaluateName);
        DebugEvaluateResult evaluation = await client.EvaluateAsync(
            new DebugEvaluateRequest(frame.Id, "localObject.Number"),
            cancellationToken).ConfigureAwait(false);
        Assert.AreEqual("42", evaluation.Result);
        Assert.AreEqual("int", evaluation.Type);
        DebugVariableInfo localArray = localVariables.Single(
            static variable => variable.Name == "localArray");
        Assert.IsNotNull(localArray.MemoryReference);
        DebugMemoryReadResult memory = await client.ReadMemoryAsync(
            new DebugMemoryReadRequest(localArray.MemoryReference, 0, 64),
            cancellationToken).ConfigureAwait(false);
        AssertRpcArrayMemory(memory);
        Assert.IsNotNull(frame.InstructionReference);
        await AssertRpcDisassemblyAsync(client, frame.InstructionReference, cancellationToken)
            .ConfigureAwait(false);
        await AssertRpcInstructionBreakpointValidationAsync(
            client,
            frame.InstructionReference,
            cancellationToken).ConfigureAwait(false);
        await AssertRpcNavigationAsync(
            client,
            frame,
            stopped,
            sourcePath,
            localLine,
            cancellationToken).ConfigureAwait(false);

        DebugSessionSnapshot terminated = await client.TerminateAsync(cancellationToken)
            .ConfigureAwait(false);
        Assert.AreEqual(DebugSessionState.Terminated, terminated.State);
    }

    private static async Task<DebugSessionSnapshot> WaitForStoppedAsync(
        DebuggerRpcClient client,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            DebugSessionSnapshot snapshot = await client.GetSessionAsync(cancellationToken)
                .ConfigureAwait(false);
            if (snapshot.State == DebugSessionState.Stopped)
            {
                return snapshot;
            }

            if (snapshot.State is DebugSessionState.Terminated or DebugSessionState.Faulted)
            {
                Assert.Fail($"The target reached {snapshot.State} before its source breakpoint.");
            }

            await Task.Delay(TimeSpan.FromMilliseconds(20), cancellationToken)
                .ConfigureAwait(false);
        }
    }

}
