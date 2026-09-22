using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

namespace Csls.Debugger.Tests;

/// <summary>
/// Exercises terminal reverse-request cancellation across real DAP and process boundaries.
/// </summary>
[TestClass]
public sealed class DapTerminalCancellationTests : DapTestContext
{
    /// <summary>
    /// Releases a canceled terminal listener and starts a fresh target on the same connection.
    /// </summary>
    [TestMethod]
    [Timeout(30000, CooperativeCancellation = true)]
    public async Task CancelUnansweredTerminalRequestThenLaunchAgain()
    {
        DapTestClient client = await DapTestClient.CreateAsync(TestContext.CancellationToken)
            .ConfigureAwait(false);
        await using ConfiguredAsyncDisposable clientDisposal = client.ConfigureAwait(false);
        try
        {
            int initialize = await client.SendInitializeRequestAsync(TestContext.CancellationToken,
                writeProperties: writer => writer.WriteBoolean("supportsRunInTerminalRequest", true))
                .ConfigureAwait(false);
            using (JsonDocument response = await client.ReadMessageAsync(TestContext.CancellationToken)
                .ConfigureAwait(false))
            {
                AssertResponse(response.RootElement, initialize, "initialize", success: true);
            }

            string program = ResolveTestProcessHost();
            int firstLaunch = await client.SendRequestAsync("launch", writer =>
            {
                writer.WriteStartObject();
                writer.WriteString("program", program);
                writer.WriteString("console", "integratedTerminal");
                writer.WriteEndObject();
            }, TestContext.CancellationToken).ConfigureAwait(false);
            using (JsonDocument initialized = await client.ReadMessageAsync(TestContext.CancellationToken)
                .ConfigureAwait(false))
            {
                AssertEvent(initialized.RootElement, "initialized");
            }

            int firstConfiguration = await client.SendRequestAsync(
                "configurationDone", WriteEmptyObject, TestContext.CancellationToken)
                .ConfigureAwait(false);
            int reverseSequence;
            using (JsonDocument reverse = await client.ReadMessageAsync(TestContext.CancellationToken)
                .ConfigureAwait(false))
            {
                Assert.AreEqual("request", reverse.RootElement.GetProperty("type").GetString());
                Assert.AreEqual("runInTerminal", reverse.RootElement.GetProperty("command").GetString());
                reverseSequence = reverse.RootElement.GetProperty("seq").GetInt32();
            }

            int cancel = await client.SendRequestAsync("cancel", writer =>
            {
                writer.WriteStartObject();
                writer.WriteNumber("requestId", firstConfiguration);
                writer.WriteEndObject();
            }, TestContext.CancellationToken).ConfigureAwait(false);
            HashSet<int> completedRequests = [];
            while (completedRequests.Count < 3)
            {
                using JsonDocument response = await client.ReadMessageAsync(TestContext.CancellationToken)
                    .ConfigureAwait(false);
                JsonElement message = response.RootElement;
                Assert.AreEqual("response", message.GetProperty("type").GetString());
                int requestSequence = message.GetProperty("request_seq").GetInt32();
                Assert.IsTrue(completedRequests.Add(requestSequence), "A canceled request answered twice.");
                if (requestSequence == cancel)
                {
                    AssertResponse(message, cancel, "cancel", success: true);
                }
                else if (requestSequence == firstConfiguration)
                {
                    AssertResponse(message, firstConfiguration, "configurationDone", success: false);
                }
                else
                {
                    AssertResponse(message, firstLaunch, "launch", success: false);
                }
            }

            _ = await client.SendResponseAsync(reverseSequence, "runInTerminal",
                success: true, message: null, TestContext.CancellationToken).ConfigureAwait(false);

            int secondLaunch = await client.SendRequestAsync("launch", writer =>
            {
                writer.WriteStartObject();
                writer.WriteString("program", program);
                writer.WriteBoolean("noDebug", true);
                writer.WriteStartArray("args");
                writer.WriteStringValue("--print-environment-and-exit");
                writer.WriteStringValue("CSLS_TERMINAL_RETRY_VALUE");
                writer.WriteStringValue("0");
                writer.WriteEndArray();
                writer.WriteStartObject("env");
                writer.WriteString("CSLS_TERMINAL_RETRY_VALUE", "retry-ok");
                writer.WriteEndObject();
                writer.WriteEndObject();
            }, TestContext.CancellationToken).ConfigureAwait(false);
            using (JsonDocument initialized = await client.ReadMessageAsync(TestContext.CancellationToken)
                .ConfigureAwait(false))
            {
                AssertEvent(initialized.RootElement, "initialized");
            }
            int secondConfiguration = await client.SendRequestAsync(
                "configurationDone", WriteEmptyObject, TestContext.CancellationToken)
                .ConfigureAwait(false);
            using (JsonDocument response = await client.ReadMessageAsync(TestContext.CancellationToken)
                .ConfigureAwait(false))
            {
                AssertResponse(response.RootElement, secondConfiguration, "configurationDone", success: true);
            }
            using (JsonDocument response = await client.ReadMessageAsync(TestContext.CancellationToken)
                .ConfigureAwait(false))
            {
                AssertResponse(response.RootElement, secondLaunch, "launch", success: true);
            }

            bool processSeen = false;
            bool exitSeen = false;
            bool terminated = false;
            var output = new StringBuilder();
            while (!terminated)
            {
                using JsonDocument message = await client.ReadMessageAsync(TestContext.CancellationToken)
                    .ConfigureAwait(false);
                JsonElement root = message.RootElement;
                string? eventName = root.GetProperty("event").GetString();
                switch (eventName)
                {
                    case "process":
                        processSeen = true;
                        break;
                    case "output":
                        _ = output.Append(root.GetProperty("body").GetProperty("output").GetString());
                        break;
                    case "exited":
                        Assert.AreEqual(0, root.GetProperty("body").GetProperty("exitCode").GetInt32());
                        exitSeen = true;
                        break;
                    case "terminated":
                        terminated = true;
                        break;
                    default:
                        Assert.Fail($"Unexpected DAP event: {root.GetRawText()}");
                        break;
                }
            }

            Assert.IsTrue(processSeen);
            Assert.IsTrue(exitSeen);
            Assert.AreEqual("retry-ok", output.ToString());
        }
        finally
        {
            TestContext.WriteLine(client.ProtocolTranscript);
            TestContext.WriteLine(client.Diagnostics.ToString());
        }
    }
}
