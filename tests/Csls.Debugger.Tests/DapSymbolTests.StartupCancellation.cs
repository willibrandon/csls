using Microsoft.Win32.SafeHandles;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Csls.Debugger.Tests;

/// <summary>
/// Verifies cancellation while a live target has disabled its runtime debugging service.
/// </summary>
public sealed partial class DapSymbolTests
{
    /// <summary>
    /// Cancels startup through either pending request or input closure and releases the launched process.
    /// </summary>
    /// <param name="cancelLaunch">Whether to cancel launch instead of configurationDone.</param>
    /// <param name="closeInput">Whether to close the protocol input instead of sending cancellation.</param>
    [TestMethod]
    [DataRow(false, false)]
    [DataRow(true, false)]
    [DataRow(false, true)]
    [Timeout(30000, CooperativeCancellation = true)]
    public async Task PendingRuntimeStartupCanBeCanceled(bool cancelLaunch, bool closeInput)
    {
        string directory = Path.Join(Path.GetTempPath(), $"csls-startup-cancellation-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            DapTestClient client = await DapTestClient.CreateAsync(TestContext.CancellationToken).ConfigureAwait(false);
            await using ConfiguredAsyncDisposable disposal = client.ConfigureAwait(false);
            int initialize = await client.SendInitializeRequestAsync(TestContext.CancellationToken).ConfigureAwait(false);
            using (JsonDocument response = await client.ReadMessageAsync(TestContext.CancellationToken).ConfigureAwait(false))
            {
                AssertResponse(response.RootElement, initialize, "initialize", success: true);
            }

            string processPath = Path.Join(directory, "process.id");
            int launch = await client.SendRequestAsync("launch", writer =>
            {
                writer.WriteStartObject();
                writer.WriteString("program", ResolveTestProcessHost());
                writer.WriteStartArray("args");
                writer.WriteStringValue("--announce-process-and-wait-for-file");
                writer.WriteStringValue(processPath);
                writer.WriteStringValue(Path.Join(directory, "release"));
                writer.WriteEndArray();
                writer.WriteStartObject("env");
                writer.WriteString("DOTNET_EnableDiagnostics_Debugger", "0");
                writer.WriteEndObject();
                writer.WriteEndObject();
            }, TestContext.CancellationToken).ConfigureAwait(false);
            using (JsonDocument initialized = await client.ReadMessageAsync(TestContext.CancellationToken).ConfigureAwait(false))
            {
                AssertEvent(initialized.RootElement, "initialized");
            }

            int configuration = await client.SendRequestAsync("configurationDone", WriteEmptyObject,
                TestContext.CancellationToken).ConfigureAwait(false);
            await client.WaitForTargetSignalAsync(processPath, configuration, TestContext.CancellationToken)
                .ConfigureAwait(false);
            int processId = await ReadTargetProcessIdAsync(processPath, TestContext.CancellationToken).ConfigureAwait(false);
            using var target = Process.GetProcessById(processId);
            Assert.IsFalse(target.HasExited);

            if (closeInput)
            {
                await client.CloseProtocolAsync().ConfigureAwait(false);
                Assert.AreEqual(0, await client.WaitForExitAsync(TestContext.CancellationToken).ConfigureAwait(false));
                Assert.AreEqual(string.Empty, client.Diagnostics.ToString());
            }
            else
            {
                int cancel = await client.SendRequestAsync("cancel", writer =>
                {
                    writer.WriteStartObject();
                    writer.WriteNumber("requestId", cancelLaunch ? launch : configuration);
                    writer.WriteEndObject();
                }, TestContext.CancellationToken).ConfigureAwait(false);
                var pending = new Dictionary<int, string>
                {
                    [cancel] = "cancel",
                    [launch] = "launch",
                    [configuration] = "configurationDone"
                };
                while (pending.Count > 0)
                {
                    using JsonDocument response = await client.ReadMessageAsync(TestContext.CancellationToken)
                        .ConfigureAwait(false);
                    JsonElement root = response.RootElement;
                    Assert.AreEqual("response", root.GetProperty("type").GetString());
                    int sequence = root.GetProperty("request_seq").GetInt32();
                    Assert.IsTrue(pending.Remove(sequence, out string? command), root.GetRawText());
                    Assert.IsNotNull(command);
                    AssertResponse(root, sequence, command, success: sequence == cancel);
                    if (sequence != cancel)
                    {
                        Assert.AreEqual("cancelled", root.GetProperty("message").GetString());
                    }
                }
            }

            await target.WaitForExitAsync(TestContext.CancellationToken).ConfigureAwait(false);
            Assert.IsTrue(target.HasExited);
            if (!closeInput)
            {
                (int threadId, _) = await LaunchAtEntryAsync(client, SymbolFixtures.EntryAppHostPath,
                    ["--print-environment", "CSLS_DEBUGGER_ENTRY_VALUE"], initializeClient: false).ConfigureAwait(false);
                await ContinueEntryToExitAsync(client, threadId, "entry-result").ConfigureAwait(false);
            }
        }
        finally
        {
            await DebuggerTestDirectoryReleaseWaiter.DeleteAsync(directory, TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Reads the published process identifier while Windows retains delete access for a rename.
    /// </summary>
    [TestMethod]
    [OSCondition(ConditionMode.Include, OperatingSystems.Windows)]
    public async Task PublishedProcessIdAllowsRenameHandle()
    {
        string directory = Directory.CreateTempSubdirectory("csls-published-process-").FullName;
        string path = Path.Join(directory, "process.id");
        try
        {
            using var target = Process.GetCurrentProcess();
            await File.WriteAllTextAsync(path, target.Id.ToString(CultureInfo.InvariantCulture),
                TestContext.CancellationToken).ConfigureAwait(false);
            using SafeFileHandle publication = File.OpenHandle(path, FileMode.Open, FileAccess.Read,
                FileShare.Read | FileShare.Delete, FileOptions.DeleteOnClose);
            Assert.AreEqual(target.Id, await ReadTargetProcessIdAsync(path, TestContext.CancellationToken)
                .ConfigureAwait(false));
        }
        finally
        {
            await DebuggerTestDirectoryReleaseWaiter.DeleteAsync(directory, TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        }
    }

    private static async Task<int> ReadTargetProcessIdAsync(string path, CancellationToken cancellationToken)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete,
            bufferSize: 4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var reader = new StreamReader(stream);
        return int.Parse(await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture);
    }
}
