using Csls.Debugger.Contracts;
using Csls.Debugger.Control;
using System.Runtime.CompilerServices;

namespace Csls.Debugger.Tests;

/// <summary>
/// Verifies the private debugger control protocol over its real local transport.
/// </summary>
[TestClass]
public sealed class DebuggerRpcTests
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
            if (Directory.Exists(testDirectory))
            {
                Directory.Delete(testDirectory, recursive: true);
            }
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
                "int localNumber = number + 1;",
                StringComparison.Ordinal))
            .Number;
        string socketPath = Path.Join(testDirectory, "debugger.sock");
        string signalPath = Path.Join(testDirectory, "continue.signal");

        var service = new DebuggerControlService();
        await using ConfiguredAsyncDisposable serviceDisposal = service.ConfigureAwait(false);
        var server = new DebuggerRpcServer(socketPath, service);
        await using ConfiguredAsyncDisposable serverDisposal = server.ConfigureAwait(false);
        server.Start();
        var client = new DebuggerRpcClient(socketPath);
        await using ConfiguredAsyncDisposable clientDisposal = client.ConfigureAwait(false);
        await client.ConnectAsync(cancellationToken).ConfigureAwait(false);

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
                Arguments = ["--debugger-fixture", signalPath]
            },
            cancellationToken).ConfigureAwait(false);
        DebugSessionSnapshot stopped = await WaitForStoppedAsync(client, cancellationToken)
            .ConfigureAwait(false);
        Assert.AreEqual("breakpoint", stopped.StopReason);
        Assert.IsNotNull(stopped.StoppedThreadId);
        Assert.IsGreaterThan(0, stopped.StoppedThreadId.Value);

        IReadOnlyList<DebugThreadInfo> threads = await client.GetThreadsAsync(cancellationToken)
            .ConfigureAwait(false);
        Assert.IsNotEmpty(threads);
        DebugStackTrace stack = await client.GetStackAsync(
            new DebugStackRequest(stopped.StoppedThreadId!.Value, 0, 64),
            cancellationToken).ConfigureAwait(false);
        DebugStackFrameInfo frame = stack.StackFrames.Single(candidate =>
            string.Equals(candidate.SourcePath, sourcePath, StringComparison.Ordinal) &&
            candidate.Line == breakpointLine);
        IReadOnlyList<DebugScopeInfo> scopes = await client.GetScopesAsync(
            new DebugScopesRequest(frame.Id),
            cancellationToken).ConfigureAwait(false);
        Assert.HasCount(2, scopes);
        DebugScopeInfo arguments = scopes.Single(static scope => scope.Name == "Arguments");
        IReadOnlyList<DebugVariableInfo> variables = await client.GetVariablesAsync(
            new DebugVariablesRequest(arguments.VariablesReference, 0, 0),
            cancellationToken).ConfigureAwait(false);
        DebugVariableInfo number = variables.Single(static variable => variable.Name == "number");
        Assert.AreEqual("42", number.Value);
        Assert.AreEqual("int", number.Type);
        DebugScopeInfo locals = scopes.Single(static scope => scope.Name == "Locals");
        IReadOnlyList<DebugVariableInfo> localVariables = await client.GetVariablesAsync(
            new DebugVariablesRequest(locals.VariablesReference, 0, 0),
            cancellationToken).ConfigureAwait(false);
        DebugVariableInfo localNumber = localVariables.Single(
            static variable => variable.Name == "localNumber");
        Assert.AreEqual("0", localNumber.Value);
        Assert.AreEqual("int", localNumber.Type);

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

    private static string ResolveTestProcessHost(string repositoryRoot) => Path.Join(
        repositoryRoot,
        "artifacts",
        "bin",
        "Csls.TestProcessHost",
        "debug",
        "csls-test-process-host.dll");

    private static string FindRepositoryRoot([CallerFilePath] string sourcePath = "")
    {
        DirectoryInfo? directory = new FileInfo(sourcePath).Directory;
        while (directory is not null && !File.Exists(Path.Join(directory.FullName, "Csls.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new DirectoryNotFoundException("Could not locate the csls repository root.");
    }
}
