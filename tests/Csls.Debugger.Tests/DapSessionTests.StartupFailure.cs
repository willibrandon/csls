using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Csls.Debugger.Tests;

/// <summary>
/// Verifies failed native host startup and subsequent managed launch through real DAP streams.
/// </summary>
public sealed partial class DapSessionTests
{
    /// <summary>
    /// Reports a host's observed exit before runtime startup and permits a corrected launch in the same adapter.
    /// </summary>
    /// <param name="missingAssembly">Whether the apphost's managed assembly is absent.</param>
    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    [Timeout(30000, CooperativeCancellation = true)]
    public async Task FailedAppHostStartupReportsExitAndAllowsAnotherLaunch(bool missingAssembly)
    {
        string directory = Path.Join(Path.GetTempPath(), $"csls-failed-apphost-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            string appHost = SymbolFixtures.EntryAppHostPath;
            string program = Path.Join(directory, Path.GetFileName(appHost));
            File.Copy(appHost, program);
            if (!missingAssembly)
            {
                File.Copy(Path.ChangeExtension(appHost, ".dll"), Path.ChangeExtension(program, ".dll"));
            }

            var probe = new ProcessStartInfo(program) { WorkingDirectory = directory };
            (int exitCode, string output, string error) = await DebuggerTestProcess.RunAsync(
                probe, TestContext.CancellationToken).ConfigureAwait(false);
            Assert.AreNotEqual(0, exitCode);
            Assert.AreEqual(string.Empty, output);
            Assert.Contains(missingAssembly ? "EntryAppHost.dll" : "EntryAppHost.runtimeconfig.json", error);

            DapTestClient client = await DapTestClient.CreateAsync(TestContext.CancellationToken).ConfigureAwait(false);
            await using ConfiguredAsyncDisposable disposal = client.ConfigureAwait(false);
            int initialize = await client.SendInitializeRequestAsync(TestContext.CancellationToken).ConfigureAwait(false);
            using (JsonDocument response = await client.ReadMessageAsync(TestContext.CancellationToken).ConfigureAwait(false))
            {
                AssertResponse(response.RootElement, initialize, "initialize", success: true);
            }

            int launch = await client.SendRequestAsync("launch", writer =>
            {
                writer.WriteStartObject();
                writer.WriteString("program", program);
                writer.WriteBoolean("stopAtEntry", true);
                writer.WriteEndObject();
            }, TestContext.CancellationToken).ConfigureAwait(false);
            using (JsonDocument response = await client.ReadMessageAsync(TestContext.CancellationToken).ConfigureAwait(false))
            {
                AssertEvent(response.RootElement, "initialized");
            }

            int configuration = await client.SendRequestAsync("configurationDone", WriteEmptyObject,
                TestContext.CancellationToken).ConfigureAwait(false);
            string expectedMessage = $"exited with code {exitCode.ToString(CultureInfo.InvariantCulture)} before CoreCLR startup";
            using (JsonDocument response = await client.ReadMessageAsync(TestContext.CancellationToken).ConfigureAwait(false))
            {
                AssertResponse(response.RootElement, configuration, "configurationDone", success: false);
                Assert.Contains(expectedMessage, response.RootElement.GetProperty("message").GetString()!);
            }

            using (JsonDocument response = await client.ReadMessageAsync(TestContext.CancellationToken).ConfigureAwait(false))
            {
                AssertResponse(response.RootElement, launch, "launch", success: false);
                Assert.Contains(expectedMessage, response.RootElement.GetProperty("message").GetString()!);
            }

            (int threadId, _) = await LaunchAtEntryAsync(client, SymbolFixtures.EntryAppHostPath,
                ["--print-environment", "CSLS_DEBUGGER_ENTRY_VALUE"], initializeClient: false).ConfigureAwait(false);
            await ContinueEntryToExitAsync(client, threadId, "entry-result").ConfigureAwait(false);
        }
        finally
        {
            await DebuggerTestDirectoryReleaseWaiter.DeleteAsync(directory, TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        }
    }
}
