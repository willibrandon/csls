using Csls.Debugger.Contracts;
using Csls.Debugger.Control;
using Csls.Debugger.Dump;
using Microsoft.Diagnostics.NETCore.Client;
using Microsoft.Diagnostics.Runtime;
using Microsoft.Diagnostics.Runtime.DataReaders.Implementation;
using System.Diagnostics;
using System.Globalization;
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
    /// Retains the native writer's filesystem error and releases the failed capture's target and directory.
    /// </summary>
    [TestMethod]
    [Timeout(30000, CooperativeCancellation = true)]
    public async Task DumpCaptureFailureRetainsNativeWriterError()
    {
        DebuggerDumpCaptureException failure = await Assert.ThrowsExactlyAsync<DebuggerDumpCaptureException>(
            () => DebuggerDumpFixture.CreateAsync(ResolveTestProcessHost(), TestContext.CancellationToken,
                blockDumpOutput: true)).ConfigureAwait(false);
        _ = Assert.IsInstanceOfType<ServerErrorException>(failure.InnerException);
        Assert.Contains(failure.DumpPath, failure.StandardError);
        Assert.Contains(failure.StandardError, failure.Message);
        Assert.IsLessThanOrEqualTo(16 * 1024, failure.StandardError.Length);
        Assert.IsLessThanOrEqualTo(16 * 1024, failure.StandardOutput.Length);
        string? directory = Path.GetDirectoryName(failure.DumpPath);
        Assert.IsNotNull(directory);
        Assert.IsFalse(Directory.Exists(directory));
        _ = Assert.ThrowsExactly<ArgumentException>(() => Process.GetProcessById(failure.ProcessId));
    }

    /// <summary>
    /// Records a real target's fatal managed stack and retains the requested crash artifacts after cleanup.
    /// </summary>
    /// <param name="captureMemory">Whether the runtime also captures native thread contexts and stack memory.</param>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    [OSCondition(OperatingSystems.Linux | OperatingSystems.OSX)]
    [Timeout(30000, CooperativeCancellation = true)]
    public async Task NativeCrashReportContainsTargetStack(bool captureMemory)
    {
        var capture = new DebuggerCrashReportCapture(TestContext, captureMemory ? DumpType.Normal : null);
        int processId;
        string reportName;
        await using (capture.ConfigureAwait(false))
        {
            Assert.IsFalse(Directory.Exists(capture.ArtifactDirectory));
            var startInfo = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet");
            startInfo.ArgumentList.Add(ResolveTestProcessHost());
            startInfo.ArgumentList.Add("--debugger-stack-overflow-fixture");
            foreach ((string name, string? value) in capture.Variables)
            {
                startInfo.Environment[name] = value;
            }

            (processId, int exitCode, string output, string error) = await DebuggerTestProcess.RunWithIdentityAsync(
                startInfo, TestContext.CancellationToken).ConfigureAwait(false);
            Assert.AreNotEqual(0, exitCode, output + error);
            Assert.Contains("overflow-ready", output, error);
            string[] artifacts = Directory.GetFiles(capture.DirectoryPath);
            Assert.HasCount(captureMemory ? 2 : 1, artifacts, output + error);
            reportName = $"process-{processId}" + (captureMemory ? ".dmp" : string.Empty) + ".crashreport.json";
            string report = Assert.ContainsSingle(artifacts.Where(path => path.EndsWith(".crashreport.json", StringComparison.Ordinal)));
            Assert.AreEqual(reportName, Path.GetFileName(report));
            using FileStream stream = File.OpenRead(report);
            using JsonDocument document = await JsonDocument.ParseAsync(stream,
                cancellationToken: TestContext.CancellationToken).ConfigureAwait(false);
            JsonElement[] crashed = [.. document.RootElement.GetProperty("payload").GetProperty("threads").EnumerateArray()
                .Where(thread => thread.GetProperty("crashed").GetString() == "true")];
            JsonElement thread = Assert.ContainsSingle(crashed);
            Assert.Contains("DebuggerDeepStackFixture.Overflow", thread.GetProperty("stack_frames").GetRawText());
            Assert.Contains("Stack overflow", error, StringComparison.OrdinalIgnoreCase);
        }

        Assert.IsFalse(Directory.Exists(capture.DirectoryPath));
        string[] retained = Directory.GetFiles(capture.ArtifactDirectory);
        Assert.HasCount(captureMemory ? 2 : 1, retained);
        Assert.Contains(reportName, retained.Select(Path.GetFileName));
        if (captureMemory)
        {
            string dump = Path.Join(capture.ArtifactDirectory, $"process-{processId}.dmp");
            Assert.Contains(dump, retained);
            using var target = DataTarget.LoadDump(dump, new DataTargetOptions { SymbolPaths = [] });
            Assert.AreEqual(processId, DumpProcessIdentity.Read(target.DataReader, dump, TestContext.CancellationToken));
            _ = Assert.ContainsSingle(target.ClrVersions);
        }
    }

    /// <summary>
    /// Retains private native allocations at a fatal target exit and cleans up the temporary capture directory.
    /// </summary>
    [TestMethod]
    [OSCondition(OperatingSystems.Linux | OperatingSystems.OSX)]
    [Timeout(30000, CooperativeCancellation = true)]
    public async Task HeapCrashCapturePreservesPrivateNativeMemory()
    {
        long started = Stopwatch.GetTimestamp();
        var capture = new DebuggerCrashReportCapture(TestContext, DumpType.WithHeap);
        int processId;
        ulong address;
        await using (capture.ConfigureAwait(false))
        {
            var startInfo = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet");
            startInfo.ArgumentList.Add(ResolveTestProcessHost());
            startInfo.ArgumentList.Add("--debugger-native-memory-crash-fixture");
            foreach ((string name, string? value) in capture.Variables)
            {
                startInfo.Environment[name] = value;
            }

            (processId, int exitCode, string output, string error) = await DebuggerTestProcess.RunWithIdentityAsync(
                startInfo, TestContext.CancellationToken,
                line => TestContext.WriteLine($"Heap crash capture {Stopwatch.GetElapsedTime(started).TotalMilliseconds:F1} ms: {line}"),
                diagnosticContext: TestContext).ConfigureAwait(false);
            Assert.AreNotEqual(0, exitCode, output + error);
            Assert.Contains("native-memory-crash", error);
            const string AddressPrefix = "native-memory-ready:";
            string announcement = Assert.ContainsSingle(output.Split('\n', StringSplitOptions.TrimEntries)
                .Where(line => line.StartsWith(AddressPrefix, StringComparison.Ordinal)));
            address = ulong.Parse(announcement.AsSpan(AddressPrefix.Length), NumberStyles.HexNumber,
                CultureInfo.InvariantCulture);
            Assert.IsGreaterThan(0UL, address);
            Assert.HasCount(2, Directory.GetFiles(capture.DirectoryPath));
        }

        Assert.IsFalse(Directory.Exists(capture.DirectoryPath));
        string dump = Path.Join(capture.ArtifactDirectory, $"process-{processId}.dmp");
        string[] retained = Directory.GetFiles(capture.ArtifactDirectory);
        Assert.HasCount(2, retained);
        Assert.Contains(dump, retained);
        Assert.Contains(dump + ".crashreport.json", retained);
        using var target = DataTarget.LoadDump(dump, new DataTargetOptions { SymbolPaths = [] });
        Assert.AreEqual(processId, DumpProcessIdentity.Read(target.DataReader, dump, TestContext.CancellationToken));
        byte[] contents = new byte[4096];
        foreach (ulong offset in new ulong[] { 0, 512 * 1024, 1024 * 1024 - 4096 })
        {
            Assert.AreEqual(contents.Length, target.DataReader.Read(checked(address + offset), contents));
            Assert.AreEqual(-1, contents.AsSpan().IndexOfAnyExcept((byte)0x5a), $"Native allocation offset {offset}.");
        }
    }

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
            string dumpPath = await LinuxDebuggerProcessCapture.CaptureAsync(worker, directory,
                TestContext.CancellationToken).ConfigureAwait(false);
            Assert.AreEqual(Path.Join(directory, $"process-{worker.Id}.dmp"), dumpPath);
            Assert.IsGreaterThan(0L, new FileInfo(dumpPath).Length);
            Assert.IsFalse(worker.HasExited);
            string kernelReport = await File.ReadAllTextAsync(Path.ChangeExtension(dumpPath, ".proc.txt"),
                TestContext.CancellationToken).ConfigureAwait(false);
            Assert.Contains($"Pid:\t{worker.Id}\n", kernelReport);
            Assert.Contains("wchan", kernelReport);
            Assert.Contains("syscall", kernelReport);
            Assert.Contains($"task/{worker.Id}/wchan", kernelReport);
            Assert.Contains("\nmaps\n", kernelReport);
            Assert.IsLessThanOrEqualTo(1024 * 1024, kernelReport.Length);
            using (var captured = DataTarget.LoadDump(dumpPath, new DataTargetOptions { SymbolPaths = [] }))
            {
                Assert.AreEqual(worker.Id, DumpProcessIdentity.Read(captured.DataReader, dumpPath,
                    TestContext.CancellationToken));
                IThreadReader threads = Assert.IsInstanceOfType<IThreadReader>(captured.DataReader);
                Assert.Contains(checked((uint)worker.Id), threads.EnumerateOSThreadIds());
                _ = Assert.ContainsSingle(captured.ClrVersions);
            }

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

            string dumpPath = await LinuxDebuggerProcessCapture.CaptureAsync(process, directory,
                TestContext.CancellationToken).ConfigureAwait(false);
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

    /// <summary>
    /// Reads the adapter and target's kernel state while preserving the stopped frame and subsequent execution.
    /// </summary>
    [TestMethod]
    [OSCondition(OperatingSystems.Linux)]
    [SupportedOSPlatform("linux")]
    [Timeout(30000, CooperativeCancellation = true)]
    public async Task KernelWaitCapturePreservesStoppedTarget()
    {
        string directory = Directory.CreateTempSubdirectory("csls-kernel-wait-capture-").FullName;
        try
        {
            DapTestClient client = await DapTestClient.CreateAsync(TestContext.CancellationToken,
                workerPath: ManagedWorkerPath).ConfigureAwait(false);
            await using ConfiguredAsyncDisposable cleanup = client.ConfigureAwait(false);
            (int thread, int processId) = await LaunchAtEntryAsync(client, ResolveTestProcessHost(),
                ["--print-environment", "CSLS_DEBUGGER_ENTRY_VALUE"]).ConfigureAwait(false);
            using var host = Process.GetProcessById(client.HostProcessId);
            using var target = Process.GetProcessById(processId);
            JsonElement beforeStack = await ReadDeepStackPageAsync(client, thread, 0, 1).ConfigureAwait(false);
            JsonElement before = Assert.ContainsSingle(beforeStack.GetProperty("stackFrames").EnumerateArray());
            foreach (Process process in new[] { host, target })
            {
                string reportPath = await LinuxDebuggerProcessCapture.CaptureKernelStateAsync(process, directory,
                    TestContext.CancellationToken).ConfigureAwait(false);
                Assert.AreEqual(Path.Join(directory, $"process-{process.Id}.proc.txt"), reportPath);
                string report = await File.ReadAllTextAsync(reportPath, TestContext.CancellationToken).ConfigureAwait(false);
                Assert.Contains($"Pid:\t{process.Id}\n", report);
                Assert.Contains($"task/{process.Id}/wchan", report);
                Assert.Contains($"task/{process.Id}/syscall", report);
                Assert.Contains("\nmaps\n", report);
                Assert.IsLessThanOrEqualTo(1024 * 1024, report.Length);
                Assert.IsFalse(process.HasExited);
            }
            Assert.HasCount(2, Directory.EnumerateFiles(directory));
            Assert.IsEmpty(Directory.EnumerateFiles(directory, "*.dmp"));
            JsonElement afterStack = await ReadDeepStackPageAsync(client, thread, 0, 1).ConfigureAwait(false);
            JsonElement after = Assert.ContainsSingle(afterStack.GetProperty("stackFrames").EnumerateArray());
            Assert.AreEqual(before.GetProperty("id").GetInt32(), after.GetProperty("id").GetInt32());
            Assert.AreEqual(before.GetProperty("line").GetInt32(), after.GetProperty("line").GetInt32());
            Assert.AreEqual(before.GetProperty("name").GetString(), after.GetProperty("name").GetString());
            await ContinueEntryToExitAsync(client, thread, "entry-result").ConfigureAwait(false);
            await target.WaitForExitAsync(TestContext.CancellationToken).ConfigureAwait(false);
            Assert.IsTrue(target.HasExited);
            Assert.IsTrue(host.HasExited);
        }
        finally
        {
            await DebuggerTestDirectoryReleaseWaiter.DeleteAsync(directory, TimeSpan.FromSeconds(10))
                .ConfigureAwait(false);
        }
    }

    private static string ManagedWorkerPath => Path.Join(FindRepositoryRoot(), "artifacts", "bin",
        "Csls.Debugger.Worker", "debug", "csls-debugger-worker.dll");

    /// <summary>
    /// Cancels native diagnostic capture before contacting a live worker and preserves its protocol and artifact directory.
    /// </summary>
    /// <param name="captureMemory">Whether capture also requests native thread contexts through the diagnostics socket.</param>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    [OSCondition(OperatingSystems.Linux)]
    [SupportedOSPlatform("linux")]
    [Timeout(30000, CooperativeCancellation = true)]
    public async Task CanceledNativeCapturePreservesWorkerAndFiles(bool captureMemory)
    {
        string directory = Directory.CreateTempSubdirectory("csls-canceled-native-capture-").FullName;
        try
        {
            DapTestClient client = await DapTestClient.CreateAsync(TestContext.CancellationToken,
                workerPath: ManagedWorkerPath).ConfigureAwait(false);
            await using ConfiguredAsyncDisposable cleanup = client.ConfigureAwait(false);
            await RequestAsync(client, "initialize", success: true).ConfigureAwait(false);
            using Process worker = LinuxDebuggerProcessTree.OpenWorker(client.HostProcessId, ManagedWorkerPath);
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.CancellationToken);
            await cancellation.CancelAsync().ConfigureAwait(false);
            OperationCanceledException canceled = await Assert.ThrowsExactlyAsync<OperationCanceledException>(() =>
                captureMemory
                    ? LinuxDebuggerProcessCapture.CaptureAsync(worker, directory, cancellation.Token)
                    : LinuxDebuggerProcessCapture.CaptureKernelStateAsync(worker, directory, cancellation.Token))
                .ConfigureAwait(false);
            Assert.AreEqual(cancellation.Token, canceled.CancellationToken);
            Assert.IsEmpty(Directory.EnumerateFileSystemEntries(directory));
            Assert.IsFalse(worker.HasExited);
            await RequestAsync(client, "threads", success: false).ConfigureAwait(false);
            await RequestAsync(client, "disconnect", success: true).ConfigureAwait(false);
            Assert.AreEqual(0, await client.WaitForExitAsync(TestContext.CancellationToken).ConfigureAwait(false));
            await worker.WaitForExitAsync(TestContext.CancellationToken).ConfigureAwait(false);
            Assert.IsTrue(worker.HasExited);
            Assert.IsEmpty(client.Diagnostics.ToString());
        }
        finally
        {
            await DebuggerTestDirectoryReleaseWaiter.DeleteAsync(directory, TimeSpan.FromSeconds(10))
                .ConfigureAwait(false);
        }
    }

    private async Task RequestAsync(DapTestClient client, string command, bool success)
    {
        int sequence = await client.SendRequestAsync(command, WriteEmptyObject, TestContext.CancellationToken)
            .ConfigureAwait(false);
        using JsonDocument response = await client.ReadMessageAsync(TestContext.CancellationToken)
            .ConfigureAwait(false);
        AssertResponse(response.RootElement, sequence, command, success);
    }
}
