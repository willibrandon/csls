using Csls.Debugger.Contracts;
using Csls.Debugger.Control;
using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace Csls.Debugger.Tests;

/// <summary>
/// Verifies native CoreCLR debugger activation through real dbgshim and process boundaries.
/// </summary>
[TestClass]
public sealed class RuntimeActivationTests
{
    /// <summary>
    /// Gets the active MSTest context and its framework-managed cancellation token.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// Launches a real managed target suspended and initializes its ICorDebug interface.
    /// </summary>
    [TestMethod]
    [Timeout(30000, CooperativeCancellation = true)]
    public async Task ManagedTargetActivatesThroughDbgShimAndCleansUpProcess()
    {
        string repositoryRoot = FindRepositoryRoot();
        string executableName = OperatingSystem.IsWindows()
            ? "csls-test-process-host.exe"
            : "csls-test-process-host";
        string program = Path.Join(
            repositoryRoot,
            "artifacts",
            "bin",
            "Csls.TestProcessHost",
            "debug",
            executableName);
        string absentSignal = Path.Join(
            Path.GetTempPath(),
            $"csls-debugger-activation-{Guid.NewGuid():N}.signal");
        DebuggerWorkerTestSession worker = await DebuggerWorkerTestSession
            .StartAsync(TestContext.CancellationToken).ConfigureAwait(false);
        await using ConfiguredAsyncDisposable workerDisposal = worker.ConfigureAwait(false);
        DebuggerRpcClient client = worker.Client;
        DebugSessionSnapshot running = await client.LaunchAsync(
            new DebugLaunchRequest
            {
                Program = program,
                WorkingDirectory = repositoryRoot,
                Arguments = ["--wait-for-file", absentSignal]
            },
            TestContext.CancellationToken).ConfigureAwait(false);
        Assert.AreEqual(DebugSessionState.Running, running.State);
        Assert.IsNotNull(running.ProcessId);
        int processId = running.ProcessId.Value;
        DebugSessionSnapshot terminated = await client
            .TerminateAsync(TestContext.CancellationToken).ConfigureAwait(false);
        Assert.AreEqual(DebugSessionState.Terminated, terminated.State);

        Assert.IsGreaterThan(0, processId);
        _ = Assert.ThrowsExactly<ArgumentException>(
            () => Process.GetProcessById(processId));
        Assert.IsFalse(File.Exists(absentSignal));
    }

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
