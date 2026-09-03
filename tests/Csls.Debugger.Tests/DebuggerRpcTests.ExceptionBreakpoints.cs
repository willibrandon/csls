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
                Arguments = ["--debugger-exception-filter-fixture", signalPath]
            },
            cancellationToken).ConfigureAwait(false);
        DebugSessionSnapshot stopped = await WaitForStoppedAsync(client, cancellationToken)
            .ConfigureAwait(false);
        Assert.AreEqual("exception", stopped.StopReason);
        Assert.IsNotNull(stopped.StoppedThreadId);
        DebugExceptionInfo exception = await client.GetExceptionInfoAsync(
            new DebugExceptionInfoRequest(stopped.StoppedThreadId.Value),
            cancellationToken).ConfigureAwait(false);
        Assert.AreEqual("System.InvalidOperationException", exception.ExceptionId);
        Assert.AreEqual(DebugExceptionBreakMode.Thrown, exception.BreakMode);
        DebugSessionSnapshot terminated = await client.TerminateAsync(cancellationToken)
            .ConfigureAwait(false);
        Assert.AreEqual(DebugSessionState.Terminated, terminated.State);
    }
}
