using Csls.Debugger.Dump;
using Microsoft.Diagnostics.Runtime;
using Microsoft.Diagnostics.Runtime.DataReaders.Implementation;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.Versioning;
using System.Text.Json;

namespace Csls.Debugger.Tests;

/// <summary>
/// Verifies independently captured Windows stacks preserve the selected processes and their DAP connection.
/// </summary>
[TestClass]
[OSCondition(OperatingSystems.Windows)]
[SupportedOSPlatform("windows")]
public sealed class DapWindowsNativeDiagnosticsTests : DapTestContext
{
    /// <summary>
    /// Captures the stopped target and adapter with their exact identities and retains a usable debugging session.
    /// </summary>
    [TestMethod]
    [Timeout(30000, CooperativeCancellation = true)]
    public async Task NativeTreeCapturePreservesStoppedSession()
    {
        DapTestClient client = await DapTestClient.CreateAsync(TestContext.CancellationToken).ConfigureAwait(false);
        await using ConfiguredAsyncDisposable cleanup = client.ConfigureAwait(false);
        (int threadId, int processId) = await LaunchAtEntryAsync(client, ResolveTestProcessHost(),
            ["--print-environment", "CSLS_DEBUGGER_ENTRY_VALUE"]).ConfigureAwait(false);
        using var targetProcess = Process.GetProcessById(processId);
        _ = targetProcess.SafeHandle;
        string directory = await WindowsDebuggerProcessCapture.CaptureTreeAsync(client.HostProcessId, TestContext)
            .ConfigureAwait(false);
        try
        {
            foreach (int id in new[] { client.HostProcessId, processId })
            {
                string dumpPath = Path.Join(directory, $"process-{id}.dmp");
                using var dump = DataTarget.LoadDump(dumpPath, new DataTargetOptions { SymbolPaths = [] });
                Assert.AreEqual(id, DumpProcessIdentity.Read(dump.DataReader, dumpPath, TestContext.CancellationToken));
                IThreadReader threads = Assert.IsInstanceOfType<IThreadReader>(dump.DataReader);
                Assert.IsNotEmpty(threads.EnumerateOSThreadIds());
                if (id == processId)
                {
                    Assert.Contains(checked((uint)threadId), threads.EnumerateOSThreadIds());
                }
            }
            Assert.IsFalse(targetProcess.HasExited);
            int request = await client.SendRequestAsync("threads", WriteEmptyObject, TestContext.CancellationToken)
                .ConfigureAwait(false);
            using JsonDocument response = await client.ReadMessageAsync(TestContext.CancellationToken).ConfigureAwait(false);
            AssertResponse(response.RootElement, request, "threads", success: true);
            Assert.Contains(threadId, response.RootElement.GetProperty("body").GetProperty("threads").EnumerateArray()
                .Select(thread => thread.GetProperty("id").GetInt32()));
            await ContinueEntryToExitAsync(client, threadId, "entry-result").ConfigureAwait(false);
            await targetProcess.WaitForExitAsync(TestContext.CancellationToken).ConfigureAwait(false);
            Assert.AreEqual(0, targetProcess.ExitCode);
        }
        finally
        {
            await DebuggerTestDirectoryReleaseWaiter.DeleteAsync(directory, TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Preserves a completed dump when a later collector is given its existing path.
    /// </summary>
    [TestMethod]
    [Timeout(30000, CooperativeCancellation = true)]
    public async Task NativeCapturePreservesExistingFileAndTarget()
    {
        string directory = Directory.CreateTempSubdirectory("csls-windows-capture-").FullName;
        try
        {
            DapTestClient client = await DapTestClient.CreateAsync(TestContext.CancellationToken).ConfigureAwait(false);
            await using ConfiguredAsyncDisposable cleanup = client.ConfigureAwait(false);
            int initialize = await client.SendInitializeRequestAsync(TestContext.CancellationToken).ConfigureAwait(false);
            using (JsonDocument response = await client.ReadMessageAsync(TestContext.CancellationToken).ConfigureAwait(false))
            {
                AssertResponse(response.RootElement, initialize, "initialize", success: true);
            }
            using var process = Process.GetProcessById(client.HostProcessId);
            _ = process.SafeHandle;
            string path = Path.Join(directory, "adapter.dmp");
            (int exitCode, string output, string error) = await WindowsDebuggerProcessCapture.CaptureAsync(
                process, path, TestContext.CancellationToken).ConfigureAwait(false);
            Assert.AreEqual(0, exitCode, output + error);
            byte[] original = await File.ReadAllBytesAsync(path, TestContext.CancellationToken).ConfigureAwait(false);
            (exitCode, output, error) = await WindowsDebuggerProcessCapture.CaptureAsync(
                process, path, TestContext.CancellationToken).ConfigureAwait(false);
            Assert.AreNotEqual(0, exitCode, output + error);
            Assert.Contains("IOException", error);
            Assert.AreSequenceEqual(original,
                await File.ReadAllBytesAsync(path, TestContext.CancellationToken).ConfigureAwait(false));
            Assert.IsFalse(process.HasExited);
            await DisconnectAsync(client).ConfigureAwait(false);
        }
        finally
        {
            await DebuggerTestDirectoryReleaseWaiter.DeleteAsync(directory, TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Rejects a stale creation-time identity before creating an output file or changing the live process.
    /// </summary>
    [TestMethod]
    [Timeout(30000, CooperativeCancellation = true)]
    public async Task NativeCaptureRejectsChangedProcessIdentity()
    {
        string directory = Directory.CreateTempSubdirectory("csls-windows-capture-identity-").FullName;
        try
        {
            using var process = Process.GetCurrentProcess();
            string path = Path.Join(directory, "unexpected.dmp");
            var startInfo = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet");
            startInfo.ArgumentList.Add(ResolveTestProcessHost());
            startInfo.ArgumentList.Add("--windows-native-dump");
            startInfo.ArgumentList.Add(process.Id.ToString(CultureInfo.InvariantCulture));
            startInfo.ArgumentList.Add((process.StartTime.ToUniversalTime().ToFileTimeUtc() + 1)
                .ToString(CultureInfo.InvariantCulture));
            startInfo.ArgumentList.Add(path);
            (int exitCode, string output, string error) = await DebuggerTestProcess.RunAsync(
                startInfo, TestContext.CancellationToken).ConfigureAwait(false);
            Assert.AreNotEqual(0, exitCode, output + error);
            Assert.Contains("The selected process identity has changed.", error);
            Assert.IsFalse(File.Exists(path));
        }
        finally
        {
            await DebuggerTestDirectoryReleaseWaiter.DeleteAsync(directory, TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        }
    }
}
