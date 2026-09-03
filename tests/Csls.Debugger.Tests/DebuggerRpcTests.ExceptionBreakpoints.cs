using Csls.Debugger.Contracts;
using Csls.Debugger.Control;
using System.Runtime.CompilerServices;

namespace Csls.Debugger.Tests;

/// <summary>
/// Verifies managed exception policy over the real private debugger transport.
/// </summary>
public sealed partial class DebuggerRpcTests
{
    /// <summary>
    /// Configures and inspects an exact exception-type stop through private RPC.
    /// </summary>
    [TestMethod]
    [Timeout(30000, CooperativeCancellation = true)]
    [OSCondition(ConditionMode.Exclude, OperatingSystems.Windows)]
    public async Task PrivateRpcConfiguresExceptionTypeBreakpoint()
    {
        string testDirectory = Path.Join(
            Path.GetTempPath(),
            $"csls-debugger-rpc-exception-{Guid.NewGuid():N}");
        Directory.CreateDirectory(testDirectory);
        try
        {
            await ExerciseRpcExceptionBreakpointAsync(
                testDirectory,
                TestContext.CancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (Directory.Exists(testDirectory))
            {
                Directory.Delete(testDirectory, recursive: true);
            }
        }
    }

    private static async Task ExerciseRpcExceptionBreakpointAsync(
        string testDirectory,
        CancellationToken cancellationToken)
    {
        string signalPath = Path.Join(testDirectory, "continue.signal");
        DebuggerWorkerTestSession worker = await DebuggerWorkerTestSession
            .StartAsync(cancellationToken).ConfigureAwait(false);
        await using ConfiguredAsyncDisposable workerDisposal = worker.ConfigureAwait(false);
        DebuggerRpcClient client = worker.Client;
        await client.SetExceptionBreakpointsAsync(
            new DebugExceptionBreakpointSetRequest(
                [
                    new DebugExceptionBreakpointRequest(
                        DebugExceptionBreakMode.Thrown,
                        ["System.InvalidOperationException"])
                ]),
            cancellationToken).ConfigureAwait(false);
        string repositoryRoot = FindRepositoryRoot();
        _ = await client.LaunchAsync(
            new DebugLaunchRequest
            {
                Program = ResolveTestProcessHost(repositoryRoot),
                WorkingDirectory = repositoryRoot,
                Arguments = ["--debugger-exception-filter-fixture", signalPath],
                SourceFileMap = CreateDefaultSourceFileMap()
            },
            cancellationToken).ConfigureAwait(false);
        DebugSessionSnapshot stopped = await WaitForStoppedAsync(client, cancellationToken)
            .ConfigureAwait(false);
        Assert.AreEqual("exception", stopped.StopReason);
        Assert.IsNotNull(stopped.StoppedThreadId);
        int stoppedThreadId = stopped.StoppedThreadId
            ?? throw new InvalidOperationException("The target did not report a stopped thread.");
        DebugExceptionInfo exception = await client.GetExceptionInfoAsync(
            new DebugExceptionInfoRequest(stoppedThreadId),
            cancellationToken).ConfigureAwait(false);
        Assert.AreEqual("System.InvalidOperationException", exception.ExceptionId);
        Assert.AreEqual(DebugExceptionBreakMode.Thrown, exception.BreakMode);
        DebugSessionSnapshot terminated = await client.TerminateAsync(cancellationToken)
            .ConfigureAwait(false);
        Assert.AreEqual(DebugSessionState.Terminated, terminated.State);
    }
}
