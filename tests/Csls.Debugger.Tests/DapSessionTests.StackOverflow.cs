using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Csls.Debugger.Tests;

/// <summary>
/// Verifies debugger survival and owned-process cleanup after fatal target stack exhaustion.
/// </summary>
public sealed partial class DapSessionTests
{
    /// <summary>
    /// Reports one failed target exit and termination while keeping adapter stdout valid DAP.
    /// </summary>
    [TestMethod]
    [TestCategory("DebuggerStress")]
    [Timeout(30000, CooperativeCancellation = true)]
    public async Task TargetStackOverflowTerminatesWithoutCrashingAdapter()
    {
        string directory = Path.Join(Path.GetTempPath(), $"csls-debugger-stack-overflow-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            DapTestClient client = await DapTestClient.CreateAsync(TestContext.CancellationToken).ConfigureAwait(false);
            await using ConfiguredAsyncDisposable disposal = client.ConfigureAwait(false);
            int initializeSequence = await client.SendRequestAsync("initialize", WriteEmptyObject,
                TestContext.CancellationToken).ConfigureAwait(false);
            using (JsonDocument initialize = await client.ReadMessageAsync(TestContext.CancellationToken).ConfigureAwait(false))
            {
                AssertResponse(initialize.RootElement, initializeSequence, "initialize", success: true);
            }

            int launchSequence = await client.SendRequestAsync("launch", writer =>
            {
                writer.WriteStartObject();
                writer.WriteString("program", ResolveTestProcessHost());
                writer.WriteString("cwd", directory);
                writer.WriteStartArray("args");
                writer.WriteStringValue("--debugger-stack-overflow-fixture");
                writer.WriteEndArray();
                writer.WriteStartObject("env");
                writer.WriteString("DOTNET_DbgEnableMiniDump", "0");
                writer.WriteString("COMPlus_DbgEnableMiniDump", "0");
                writer.WriteEndObject();
                writer.WriteEndObject();
            }, TestContext.CancellationToken).ConfigureAwait(false);
            using (JsonDocument initialized = await client.ReadMessageAsync(TestContext.CancellationToken).ConfigureAwait(false))
            {
                AssertEvent(initialized.RootElement, "initialized");
            }

            int configurationSequence = await client.SendRequestAsync("configurationDone", WriteEmptyObject,
                TestContext.CancellationToken).ConfigureAwait(false);
            bool launched = false;
            bool configured = false;
            bool exited = false;
            bool overflowReported = false;
            bool overflowStopped = false;
            bool continued = false;
            bool continuationAcknowledged = false;
            string outputTail = string.Empty;
            int processId = 0;
            int continueSequence = 0;
            while (true)
            {
                using JsonDocument message = await client.ReadMessageAsync(TestContext.CancellationToken).ConfigureAwait(false);
                JsonElement root = message.RootElement;
                if (root.TryGetProperty("request_seq", out JsonElement sequence))
                {
                    int request = sequence.GetInt32();
                    Assert.Contains(request, new[] { launchSequence, configurationSequence, continueSequence });
                    string command = request == launchSequence ? "launch" :
                        request == configurationSequence ? "configurationDone" : "continue";
                    AssertResponse(root, request, command, success: true);
                    launched |= request == launchSequence;
                    configured |= request == configurationSequence;
                    continuationAcknowledged |= request == continueSequence;
                    continue;
                }

                switch (root.GetProperty("event").GetString())
                {
                    case "stopped":
                        Assert.IsFalse(overflowStopped, "The unhandled overflow must produce one stop.");
                        Assert.AreEqual("exception", root.GetProperty("body").GetProperty("reason").GetString());
                        Assert.AreEqual("System.StackOverflowException", root.GetProperty("body").GetProperty("text").GetString());
                        overflowStopped = true;
                        int threadId = root.GetProperty("body").GetProperty("threadId").GetInt32();
                        JsonElement stack = await ReadDeepStackPageAsync(client, threadId, 0, 1).ConfigureAwait(false);
                        Assert.AreEqual(1, stack.GetProperty("stackFrames").GetArrayLength());
                        continueSequence = await client.SendRequestAsync("continue", WriteEmptyObject,
                            TestContext.CancellationToken).ConfigureAwait(false);
                        break;
                    case "process":
                        processId = root.GetProperty("body").GetProperty("systemProcessId").GetInt32();
                        break;
                    case "continued":
                        Assert.IsFalse(continued, "Resuming the fatal target must produce one continued event.");
                        continued = true;
                        break;
                    case "output":
                        string? output = root.GetProperty("body").GetProperty("output").GetString();
                        string combined = outputTail + output;
                        overflowReported |= combined.Contains("Stack overflow", StringComparison.OrdinalIgnoreCase);
                        outputTail = combined[^Math.Min(32, combined.Length)..];
                        break;
                    case "exited":
                        Assert.IsFalse(exited, "A fatal target must have only one exited event.");
                        Assert.AreNotEqual(0, root.GetProperty("body").GetProperty("exitCode").GetInt32());
                        exited = true;
                        break;
                    case "terminated":
                        Assert.IsTrue(exited, "The target exit must precede termination.");
                        Assert.IsTrue(launched);
                        Assert.IsTrue(configured);
                        Assert.IsTrue(overflowStopped,
                            $"The target terminated before an exception stop. Recent protocol messages:{Environment.NewLine}" +
                            $"{client.ProtocolTranscript}{Environment.NewLine}Adapter diagnostics: {client.Diagnostics}");
                        Assert.IsTrue(continued);
                        Assert.IsTrue(continuationAcknowledged);
                        Assert.IsTrue(overflowReported, "The real runtime must report stack exhaustion.");
                        Assert.IsGreaterThan(0, processId);
                        Assert.AreEqual(0, await client.WaitForExitAsync(TestContext.CancellationToken).ConfigureAwait(false));
                        await AssertProcessExitedAsync(processId, TestContext.CancellationToken).ConfigureAwait(false);
                        _ = await Assert.ThrowsExactlyAsync<EndOfStreamException>(async () =>
                        {
                            using JsonDocument unexpected = await client.ReadMessageAsync(TestContext.CancellationToken)
                                .ConfigureAwait(false);
                        }).ConfigureAwait(false);
                        Assert.AreEqual(string.Empty, client.Diagnostics.ToString());
                        return;
                    default:
                        Assert.Fail($"Unexpected debugger event during stack overflow: {root}");
                        break;
                }
            }
        }
        finally
        {
            await DebuggerTestDirectoryReleaseWaiter.DeleteAsync(directory, TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        }
    }
}
