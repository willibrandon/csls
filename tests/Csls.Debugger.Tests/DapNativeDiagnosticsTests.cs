using Csls.Debugger.Contracts;
using Csls.Debugger.Control;
using Microsoft.Diagnostics.NETCore.Client;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.Versioning;
using System.Text.Json;

namespace Csls.Debugger.Tests;

/// <summary>
/// Verifies native runtime diagnostics remain separate from the worker's protocol connections.
/// </summary>
[TestClass]
public sealed class DapNativeDiagnosticsTests : DapTestContext
{
    /// <summary>
    /// Captures the actual worker while preserving subsequent DAP responses and orderly shutdown.
    /// </summary>
    [TestMethod]
    [OSCondition(OperatingSystems.Linux)]
    [SupportedOSPlatform("linux")]
    [Timeout(60000, CooperativeCancellation = true)]
    public async Task NativeDumpCapturePreservesProtocolOutput()
    {
        string directory = Directory.CreateTempSubdirectory("csls-dap-native-diagnostics-").FullName;
        try
        {
            DapTestClient client = await DapTestClient.CreateAsync(TestContext.CancellationToken,
                workerPath: ManagedWorkerPath).ConfigureAwait(false);
            await using ConfiguredAsyncDisposable cleanup = client.ConfigureAwait(false);
            await RequestAsync(client, "initialize", success: true).ConfigureAwait(false);
            using Process worker = LinuxDebuggerProcessTree.OpenWorker(client.HostProcessId, ManagedWorkerPath);
            string dumpPath = Path.Join(directory, "worker.dmp");
            await new DiagnosticsClient(worker.Id).WriteDumpAsync(DumpType.Normal, dumpPath,
                logDumpGeneration: false, TestContext.CancellationToken).ConfigureAwait(false);
            Assert.IsGreaterThan(0L, new FileInfo(dumpPath).Length);
            Assert.IsFalse(worker.HasExited);

            await RequestAsync(client, "threads", success: false).ConfigureAwait(false);
            await RequestAsync(client, "disconnect", success: true).ConfigureAwait(false);
            Assert.AreEqual(0, await client.WaitForExitAsync(TestContext.CancellationToken).ConfigureAwait(false));
            await worker.WaitForExitAsync(TestContext.CancellationToken).ConfigureAwait(false);
            Assert.IsTrue(worker.HasExited);
            Assert.Contains("[createdump]", client.Diagnostics.ToString());
            Assert.Contains(dumpPath, client.Diagnostics.ToString());
            _ = await Assert.ThrowsExactlyAsync<EndOfStreamException>(async () =>
            {
                using JsonDocument unexpected = await client.ReadMessageAsync(TestContext.CancellationToken)
                    .ConfigureAwait(false);
            }).ConfigureAwait(false);
        }
        finally
        {
            await DebuggerTestDirectoryReleaseWaiter.DeleteAsync(directory, TimeSpan.FromSeconds(10))
                .ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Preserves the actual private RPC connection and worker ownership while native diagnostics run.
    /// </summary>
    [TestMethod]
    [OSCondition(OperatingSystems.Linux)]
    [SupportedOSPlatform("linux")]
    [Timeout(60000, CooperativeCancellation = true)]
    public async Task NativeDumpCapturePreservesPrivateControlConnection()
    {
        string directory = Directory.CreateTempSubdirectory("csls-rpc-native-diagnostics-").FullName;
        try
        {
            DebuggerWorkerProcess worker = await DebuggerWorkerProcess.StartAsync(ManagedWorkerPath,
                configureNativeEnvironment: true, TestContext.CancellationToken).ConfigureAwait(false);
            await using ConfiguredAsyncDisposable cleanup = worker.ConfigureAwait(false);
            using var process = Process.GetProcessById(worker.ProcessId);
            DebugSessionSnapshot before = await worker.Client.GetSessionAsync(TestContext.CancellationToken)
                .ConfigureAwait(false);
            Assert.AreEqual(DebugSessionState.Created, before.State);
            Assert.IsNull(before.ProcessId);

            string dumpPath = Path.Join(directory, "worker.dmp");
            await new DiagnosticsClient(process.Id).WriteDumpAsync(DumpType.Normal, dumpPath,
                logDumpGeneration: false, TestContext.CancellationToken).ConfigureAwait(false);
            Assert.IsGreaterThan(0L, new FileInfo(dumpPath).Length);
            Assert.IsFalse(process.HasExited);
            DebugSessionSnapshot after = await worker.Client.GetSessionAsync(TestContext.CancellationToken)
                .ConfigureAwait(false);
            Assert.AreEqual(DebugSessionState.Created, after.State);
            Assert.IsNull(after.ProcessId);
            Assert.AreEqual(before.StopGeneration, after.StopGeneration);

            await worker.DisposeAsync().ConfigureAwait(false);
            await process.WaitForExitAsync(TestContext.CancellationToken).ConfigureAwait(false);
            Assert.IsTrue(process.HasExited);
            _ = await Assert.ThrowsExactlyAsync<ObjectDisposedException>(
                () => worker.Client.GetSessionAsync(TestContext.CancellationToken)).ConfigureAwait(false);
        }
        finally
        {
            await DebuggerTestDirectoryReleaseWaiter.DeleteAsync(directory, TimeSpan.FromSeconds(10))
                .ConfigureAwait(false);
        }
    }

    private static string ManagedWorkerPath => Path.Join(FindRepositoryRoot(), "artifacts", "bin",
        "Csls.Debugger.Worker", "debug", "csls-debugger-worker.dll");

    private async Task RequestAsync(DapTestClient client, string command, bool success)
    {
        int sequence = await client.SendRequestAsync(command, WriteEmptyObject, TestContext.CancellationToken)
            .ConfigureAwait(false);
        using JsonDocument response = await client.ReadMessageAsync(TestContext.CancellationToken)
            .ConfigureAwait(false);
        AssertResponse(response.RootElement, sequence, command, success);
    }
}
