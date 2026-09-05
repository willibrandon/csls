using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Csls.Debugger.Tests;

/// <summary>
/// Verifies cancellation while a live target has disabled its runtime debugging service.
/// </summary>
public sealed partial class DapSessionTests
{
    /// <summary>
    /// Cancels a deferred launch before configuration completes and reuses the adapter for a new target.
    /// </summary>
    [TestMethod]
    [Timeout(30000, CooperativeCancellation = true)]
    public async Task DeferredLaunchCanBeCanceledBeforeConfigurationDone()
    {
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
            writer.WriteString("program", ResolveTestProcessHost());
            writer.WriteEndObject();
        }, TestContext.CancellationToken).ConfigureAwait(false);
        using (JsonDocument initialized = await client.ReadMessageAsync(TestContext.CancellationToken).ConfigureAwait(false))
        {
            AssertEvent(initialized.RootElement, "initialized");
        }

        int cancel = await client.SendRequestAsync("cancel", writer =>
        {
            writer.WriteStartObject();
            writer.WriteNumber("requestId", launch);
            writer.WriteEndObject();
        }, TestContext.CancellationToken).ConfigureAwait(false);
        var pending = new Dictionary<int, string> { [cancel] = "cancel", [launch] = "launch" };
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
            if (sequence == launch)
            {
                Assert.AreEqual("cancelled", root.GetProperty("message").GetString());
            }
        }

        int configuration = await client.SendRequestAsync("configurationDone", WriteEmptyObject,
            TestContext.CancellationToken).ConfigureAwait(false);
        using (JsonDocument response = await client.ReadMessageAsync(TestContext.CancellationToken).ConfigureAwait(false))
        {
            AssertResponse(response.RootElement, configuration, "configurationDone", success: false);
        }

        Assert.IsNull(client.TargetProcessId);
        (int threadId, _) = await LaunchAtEntryAsync(client, ResolveTestProcessHost(),
            ["--print-environment", "CSLS_DEBUGGER_ENTRY_VALUE"], initializeClient: false).ConfigureAwait(false);
        await ContinueEntryToExitAsync(client, threadId, "entry-result").ConfigureAwait(false);
        Assert.AreEqual(0, await client.WaitForExitAsync(TestContext.CancellationToken).ConfigureAwait(false));
        Assert.AreEqual(string.Empty, client.Diagnostics.ToString());
    }

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
            int processId = int.Parse(await File.ReadAllTextAsync(processPath, TestContext.CancellationToken)
                .ConfigureAwait(false), CultureInfo.InvariantCulture);
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
}
