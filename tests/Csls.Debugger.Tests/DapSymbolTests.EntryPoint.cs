using System.Runtime.CompilerServices;

namespace Csls.Debugger.Tests;

/// <summary>
/// Verifies entry stopping through real managed launches and DAP streams.
/// </summary>
public sealed partial class DapSymbolTests
{
    /// <summary>
    /// Enters the authored async top-level method before it produces target output.
    /// </summary>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    [Timeout(30000, CooperativeCancellation = true)]
    public async Task StopAtEntryUsesAsyncTopLevelSource(bool useAppHost)
    {
        DapTestClient client = await DapTestClient.CreateAsync(TestContext.CancellationToken)
            .ConfigureAwait(false);
        await using ConfiguredAsyncDisposable disposal = client.ConfigureAwait(false);
        try
        {
            string source = Path.Join(FindRepositoryRoot(), "tests", "Csls.TestProcessHost", "Program.cs");
            int expectedLine = FindSourceLine(await File.ReadAllLinesAsync(source, TestContext.CancellationToken)
                .ConfigureAwait(false), "if (args is [\"--unix-wait-status-fixture\"");
            string program = useAppHost
                ? SymbolFixtures.EntryAppHostPath
                : ResolveTestProcessHost();
            Assert.IsTrue(File.Exists(program), $"The entry fixture was not built: {program}");
            (int threadId, _) = await LaunchAtEntryAsync(client, program,
                ["--print-environment", "CSLS_DEBUGGER_ENTRY_VALUE"]).ConfigureAwait(false);
            (_, string? path, int line) = await ReadSourceFrameAsync(
                client, threadId, source, TestContext.CancellationToken).ConfigureAwait(false);
            Assert.IsTrue(DebuggerTestPath.AreEquivalent(source, path), $"Expected source '{source}', received '{path}'.");
            Assert.AreEqual(expectedLine, line);
            await ContinueEntryToExitAsync(client, threadId, "entry-result").ConfigureAwait(false);
        }
        catch
        {
            TestContext.WriteLine($"Adapter process: {client.HostProcessId}; target process: {client.TargetProcessId}.");
            TestContext.WriteLine($"Adapter diagnostics: {client.Diagnostics}");
            TestContext.WriteLine($"Recent protocol messages:{Environment.NewLine}{client.ProtocolTranscript}");
            await DebuggerProcessDiagnostics.CaptureAsync(client.HostProcessId, TestContext).ConfigureAwait(false);
            throw;
        }
    }
}
