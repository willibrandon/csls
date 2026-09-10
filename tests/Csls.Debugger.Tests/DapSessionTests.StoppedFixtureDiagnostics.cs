using Csls.Debugger.Dump;
using Microsoft.Diagnostics.Runtime;
using Microsoft.Diagnostics.Runtime.DataReaders.Implementation;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.Versioning;
using System.Text.Json;

namespace Csls.Debugger.Tests;

/// <summary>
/// Verifies missing fixture frames retain native evidence before releasing the stopped target.
/// </summary>
public sealed partial class DapSessionTests
{
    /// <summary>
    /// Preserves the original missing-frame assertion, native target identity, and usable DAP connection.
    /// </summary>
    [TestMethod]
    [OSCondition(OperatingSystems.Windows)]
    [SupportedOSPlatform("windows")]
    [Timeout(30000, CooperativeCancellation = true)]
    public async Task MissingFixtureFramePreservesNativeEvidenceAndSession()
    {
        DapTestClient client = await DapTestClient.CreateAsync(TestContext.CancellationToken).ConfigureAwait(false);
        await using ConfiguredAsyncDisposable cleanup = client.ConfigureAwait(false);
        (int threadId, int processId) = await LaunchAtEntryAsync(client, ResolveTestProcessHost(),
            ["--print-environment", "CSLS_DEBUGGER_ENTRY_VALUE"]).ConfigureAwait(false);
        using var target = Process.GetProcessById(processId);
        _ = target.SafeHandle;
        string results = Path.Join(FindRepositoryRoot(), "artifacts", "test-results");
        string pattern = $"native-stacks-{client.HostProcessId}-*";
        HashSet<string> previousCaptures = Directory.Exists(results)
            ? [.. Directory.EnumerateDirectories(results, pattern)] : [];
        AssertFailedException failure = await Assert.ThrowsExactlyAsync<AssertFailedException>(
            () => GetFixtureFrameAsync(client)).ConfigureAwait(false);
        Assert.Contains("No managed stack frame resolved to the debugger fixture.", failure.Message);
        Assert.Contains("Recent protocol messages:", failure.Message);
        Assert.Contains("stackFrames", failure.Message);
        string directory = Assert.ContainsSingle(Directory.EnumerateDirectories(results, pattern)
            .Where(path => !previousCaptures.Contains(path)));
        try
        {
            string path = Path.Join(directory, $"process-{processId}.dmp");
            using (var dump = DataTarget.LoadDump(path, new DataTargetOptions { SymbolPaths = [] }))
            {
                Assert.AreEqual(processId, DumpProcessIdentity.Read(dump.DataReader, path, TestContext.CancellationToken));
                IThreadReader threads = Assert.IsInstanceOfType<IThreadReader>(dump.DataReader);
                Assert.Contains(checked((uint)threadId), threads.EnumerateOSThreadIds());
            }
            Assert.IsFalse(target.HasExited);
            int request = await client.SendRequestAsync("threads", WriteEmptyObject, TestContext.CancellationToken)
                .ConfigureAwait(false);
            using (JsonDocument response = await client.ReadMessageAsync(TestContext.CancellationToken).ConfigureAwait(false))
            {
                AssertResponse(response.RootElement, request, "threads", success: true);
                Assert.Contains(threadId, response.RootElement.GetProperty("body").GetProperty("threads").EnumerateArray()
                    .Select(thread => thread.GetProperty("id").GetInt32()));
            }
            await ContinueEntryToExitAsync(client, threadId, "entry-result").ConfigureAwait(false);
            await target.WaitForExitAsync(TestContext.CancellationToken).ConfigureAwait(false);
            Assert.AreEqual(0, target.ExitCode);
        }
        finally
        {
            await DebuggerTestDirectoryReleaseWaiter.DeleteAsync(directory, TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        }
    }
}
