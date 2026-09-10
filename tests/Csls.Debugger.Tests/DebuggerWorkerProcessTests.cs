using Csls.Debugger.Contracts;
using Csls.Debugger.Control;
using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace Csls.Debugger.Tests;

/// <summary>
/// Verifies shared worker supervision through real private pipes and managed processes.
/// </summary>
[TestClass]
public sealed class DebuggerWorkerProcessTests
{
    /// <summary>
    /// Gets test cancellation and diagnostic output.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// Connects to the actual worker and releases its process and RPC transport exactly once.
    /// </summary>
    [TestMethod]
    [Timeout(30000, CooperativeCancellation = true)]
    public async Task WorkerConnectsAndDisposesItsProcess()
    {
        DebuggerWorkerProcess worker = await StartWorkerAsync().ConfigureAwait(false);
        await using ConfiguredAsyncDisposable workerDisposal = worker.ConfigureAwait(false);
        using var process = Process.GetProcessById(worker.ProcessId);
        DebugSessionSnapshot snapshot = await worker.Client.GetSessionAsync(TestContext.CancellationToken)
            .ConfigureAwait(false);
        Assert.AreEqual(DebugSessionState.Created, snapshot.State);
        Assert.IsNull(snapshot.ProcessId);
        Assert.IsFalse(process.HasExited);

        await worker.DisposeAsync().ConfigureAwait(false);
        await worker.DisposeAsync().ConfigureAwait(false);
        await process.WaitForExitAsync(TestContext.CancellationToken).ConfigureAwait(false);
        Assert.IsTrue(process.HasExited);
        _ = await Assert.ThrowsExactlyAsync<ObjectDisposedException>(
            () => worker.Client.GetSessionAsync(TestContext.CancellationToken)).ConfigureAwait(false);
    }

    /// <summary>
    /// Closing the owning worker connection ends its launched managed target.
    /// </summary>
    [TestMethod]
    [Timeout(30000, CooperativeCancellation = true)]
    public async Task WorkerDisposalTerminatesItsLaunchedTarget()
    {
        DirectoryInfo directory = Directory.CreateTempSubdirectory("csls-worker-owned-");
        try
        {
            DebuggerWorkerProcess worker = await StartWorkerAsync().ConfigureAwait(false);
            await using ConfiguredAsyncDisposable workerDisposal = worker.ConfigureAwait(false);
            using var process = Process.GetProcessById(worker.ProcessId);
            DebugSessionSnapshot snapshot = await worker.Client.LaunchAsync(new DebugLaunchRequest
            {
                Program = ResolveTestHost(),
                WorkingDirectory = directory.FullName,
                Arguments = ["--announce-and-spin-until-file", Path.Join(directory.FullName, "continue.signal")]
            }, TestContext.CancellationToken).ConfigureAwait(false);
            Assert.AreEqual(DebugSessionState.Running, snapshot.State);
            int processId = snapshot.ProcessId
                ?? throw new AssertFailedException("The launch did not identify its target.");
            using var target = Process.GetProcessById(processId);
            Assert.IsFalse(target.HasExited);

            await worker.DisposeAsync().ConfigureAwait(false);
            await target.WaitForExitAsync(TestContext.CancellationToken).ConfigureAwait(false);
            await process.WaitForExitAsync(TestContext.CancellationToken).ConfigureAwait(false);
            Assert.IsTrue(target.HasExited);
            Assert.IsTrue(process.HasExited);
        }
        finally
        {
            directory.Delete();
        }
    }

    /// <summary>
    /// Closing an attached worker releases and resumes the independently owned target.
    /// </summary>
    [TestMethod]
    [Timeout(30000, CooperativeCancellation = true)]
    public async Task WorkerDisposalLeavesAttachedTargetRunning()
    {
        DirectoryInfo directory = Directory.CreateTempSubdirectory("csls-worker-attached-");
        string signalPath = Path.Join(directory.FullName, "continue.signal");
        try
        {
            var startInfo = new ProcessStartInfo("dotnet")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };
            startInfo.ArgumentList.Add(ResolveTestHost());
            startInfo.ArgumentList.Add("--announce-and-spin-until-file");
            startInfo.ArgumentList.Add(signalPath);
            using Process target = Process.Start(startInfo)
                ?? throw new AssertFailedException("The independent managed target did not start.");
            try
            {
                char[] ready = new char[5];
                Assert.AreEqual(ready.Length,
                    await target.StandardOutput.ReadBlockAsync(ready, TestContext.CancellationToken).ConfigureAwait(false));
                Assert.AreEqual("ready", new string(ready));
                DebuggerWorkerProcess worker = await StartWorkerAsync().ConfigureAwait(false);
                await using ConfiguredAsyncDisposable workerDisposal = worker.ConfigureAwait(false);
                using var process = Process.GetProcessById(worker.ProcessId);
                DebugSessionSnapshot attached = await worker.Client.AttachAsync(
                    new DebugAttachRequest(target.Id), TestContext.CancellationToken).ConfigureAwait(false);
                Assert.AreEqual(target.Id, attached.ProcessId);
                DebugSessionSnapshot stopped = await worker.Client.PauseAsync(TestContext.CancellationToken).ConfigureAwait(false);
                Assert.AreEqual(DebugSessionState.Stopped, stopped.State);
                await worker.DisposeAsync().ConfigureAwait(false);
                await process.WaitForExitAsync(TestContext.CancellationToken).ConfigureAwait(false);
                Assert.IsTrue(process.HasExited);
                Assert.IsFalse(target.HasExited);
                await File.WriteAllTextAsync(signalPath, string.Empty, TestContext.CancellationToken).ConfigureAwait(false);
                await target.WaitForExitAsync(TestContext.CancellationToken).ConfigureAwait(false);
                Assert.AreEqual(0, target.ExitCode);
            }
            finally
            {
                if (!target.HasExited)
                {
                    target.Kill(entireProcessTree: true);
                    await target.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            File.Delete(signalPath);
            directory.Delete();
        }
    }

    /// <summary>
    /// Rejects relative and missing worker paths before a process can be started.
    /// </summary>
    [TestMethod]
    [Timeout(30000, CooperativeCancellation = true)]
    public async Task WorkerStartupRequiresAbsoluteExistingPath()
    {
        _ = await Assert.ThrowsExactlyAsync<ArgumentException>(() => DebuggerWorkerProcess.StartAsync(
            "csls-debugger-worker.dll", true, TestContext.CancellationToken)).ConfigureAwait(false);
        DirectoryInfo directory = Directory.CreateTempSubdirectory("csls-worker-missing-");
        try
        {
            string missing = Path.Join(directory.FullName, "missing-worker.dll");
            FileNotFoundException exception = await Assert.ThrowsExactlyAsync<FileNotFoundException>(() =>
                DebuggerWorkerProcess.StartAsync(missing, true, TestContext.CancellationToken)).ConfigureAwait(false);
            Assert.AreEqual(missing, exception.FileName);
        }
        finally
        {
            directory.Delete();
        }
    }

    /// <summary>
    /// Honors already-canceled connection requests before starting a worker.
    /// </summary>
    [TestMethod]
    [Timeout(30000, CooperativeCancellation = true)]
    public async Task CanceledWorkerStartupDoesNotConnect()
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.CancellationToken);
        await cancellation.CancelAsync().ConfigureAwait(false);
        OperationCanceledException exception = await Assert.ThrowsExactlyAsync<OperationCanceledException>(() =>
            DebuggerWorkerProcess.StartAsync(ResolveWorker(), true, cancellation.Token)).ConfigureAwait(false);
        Assert.AreEqual(cancellation.Token, exception.CancellationToken);
    }

    /// <summary>
    /// Reports a real process's failed control startup and permits a subsequent valid connection.
    /// </summary>
    [TestMethod]
    [Timeout(30000, CooperativeCancellation = true)]
    public async Task WorkerStartupPreservesProcessFailureDiagnostics()
    {
        InvalidDataException exception = await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
            DebuggerWorkerProcess.StartAsync(ResolveTestHost(), false, TestContext.CancellationToken)).ConfigureAwait(false);
        Assert.StartsWith("Debugger worker exited with code 2:", exception.Message);
        Assert.Contains("Usage: csls-test-process-host", exception.Message);
        DebuggerWorkerProcess worker = await StartWorkerAsync().ConfigureAwait(false);
        await using ConfiguredAsyncDisposable workerDisposal = worker.ConfigureAwait(false);
        DebugSessionSnapshot snapshot = await worker.Client.GetSessionAsync(TestContext.CancellationToken).ConfigureAwait(false);
        Assert.AreEqual(DebugSessionState.Created, snapshot.State);
    }

    /// <summary>
    /// Reports unexpected worker death while still releasing its client and process ownership.
    /// </summary>
    [TestMethod]
    [Timeout(30000, CooperativeCancellation = true)]
    public async Task WorkerDisposalReportsUnexpectedProcessDeath()
    {
        DebuggerWorkerProcess worker = await StartWorkerAsync().ConfigureAwait(false);
        await using ConfiguredAsyncDisposable workerDisposal = worker.ConfigureAwait(false);
        using var process = Process.GetProcessById(worker.ProcessId);
        process.Kill(entireProcessTree: true);
        await process.WaitForExitAsync(TestContext.CancellationToken).ConfigureAwait(false);
        Assert.IsTrue(process.HasExited);
        InvalidDataException exception = await Assert.ThrowsExactlyAsync<InvalidDataException>(
            () => worker.DisposeAsync().AsTask()).ConfigureAwait(false);
        Assert.StartsWith("Debugger worker exited with code ", exception.Message);
        Assert.DoesNotContain("exited with code 0:", exception.Message);
        await worker.DisposeAsync().ConfigureAwait(false);
        _ = await Assert.ThrowsExactlyAsync<ObjectDisposedException>(
            () => worker.Client.GetSessionAsync(TestContext.CancellationToken)).ConfigureAwait(false);
    }

    private Task<DebuggerWorkerProcess> StartWorkerAsync() => DebuggerWorkerProcess.StartAsync(
        ResolveWorker(), configureNativeEnvironment: true, TestContext.CancellationToken);

    private static string ResolveWorker()
    {
        string? configured = Environment.GetEnvironmentVariable("CSLS_DEBUGGER_WORKER_TEST_PATH");
        return string.IsNullOrWhiteSpace(configured)
            ? Path.Join(DebuggerTestEnvironment.FindRepositoryRoot(), "artifacts", "bin",
                "Csls.Debugger.Worker", "debug", "csls-debugger-worker.dll")
            : Path.GetFullPath(configured);
    }

    private static string ResolveTestHost() => Path.Join(DebuggerTestEnvironment.FindRepositoryRoot(),
        "artifacts", "bin", "Csls.TestProcessHost", "debug", "csls-test-process-host.dll");
}
