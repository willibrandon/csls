using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

namespace Csls.Debugger.Tests;

/// <summary>
/// Verifies ordered request scheduling and responsive cancellation through real DAP pipes.
/// </summary>
public sealed partial class DapSessionTests
{
    /// <summary>
    /// Preserves successful variable and thread inspection when an editor pipelines its refresh.
    /// </summary>
    [TestMethod]
    [Timeout(30000, CooperativeCancellation = true)]
    public async Task PipelinedInspectionPreservesResponsesAndStoppedFrame()
    {
        string waitPath = CreateResultsViewSignalPath();
        try
        {
            DapTestClient client = await StartProxyFixtureAsync(waitPath).ConfigureAwait(false);
            await using ConfiguredAsyncDisposable disposal = client.ConfigureAwait(false);
            JsonElement frame = await GetFixtureFrameAsync(client).ConfigureAwait(false);
            int frameId = frame.GetProperty("id").GetInt32();
            int scopeSequence = await client.SendRequestAsync("scopes",
                writer => WriteFrameArguments(writer, frameId), TestContext.CancellationToken)
                .ConfigureAwait(false);
            using JsonDocument scopes = await client.ReadMessageAsync(TestContext.CancellationToken)
                .ConfigureAwait(false);
            AssertResponse(scopes.RootElement, scopeSequence, "scopes", success: true);
            int localsReference = scopes.RootElement.GetProperty("body").GetProperty("scopes")
                .EnumerateArray().Single(scope => scope.GetProperty("name").GetString() == "Locals")
                .GetProperty("variablesReference").GetInt32();
            List<(int Sequence, string Command)> requests = [];
            for (int index = 0; index < 16; index++)
            {
                requests.Add((await client.SendRequestAsync("variables",
                    writer => WriteResultsViewReference(writer, localsReference),
                    TestContext.CancellationToken).ConfigureAwait(false), "variables"));
                requests.Add((await client.SendRequestAsync("threads", WriteEmptyObject,
                    TestContext.CancellationToken).ConfigureAwait(false), "threads"));
            }

            foreach ((int sequence, string command) in requests)
            {
                using JsonDocument response = await client.ReadMessageAsync(TestContext.CancellationToken)
                    .ConfigureAwait(false);
                AssertResponse(response.RootElement, sequence, command, success: true);
                JsonElement body = response.RootElement.GetProperty("body");
                if (command == "variables")
                {
                    JsonElement number = body.GetProperty("variables").EnumerateArray()
                        .Single(variable => variable.GetProperty("name").GetString() == "localNumber");
                    Assert.AreEqual("43", number.GetProperty("value").GetString());
                    Assert.AreEqual("int", number.GetProperty("type").GetString());
                }
                else
                {
                    Assert.IsNotEmpty(body.GetProperty("threads").EnumerateArray());
                }
            }

            JsonElement unchanged = await GetFixtureFrameAsync(client).ConfigureAwait(false);
            Assert.AreEqual(frameId, unchanged.GetProperty("id").GetInt32());
            await FinishResultsViewSessionAsync(client).ConfigureAwait(false);
        }
        finally
        {
            File.Delete(waitPath);
        }
    }

    /// <summary>
    /// Drains accepted inspection requests after cancellation without blocking the cancellation channel.
    /// </summary>
    /// <param name="queuedCount">The number of inspection requests sent during target execution.</param>
    /// <param name="cancelMiddle">Whether to cancel and replace a middle request before draining.</param>
    /// <param name="waitForDeadline">Whether the real evaluation deadline starts abort before the final request is queued.</param>
    [TestMethod]
    [DataRow(1, false, false)]
    [DataRow(64, false, false)]
    [DataRow(65, false, false)]
    [DataRow(64, true, false)]
    [DataRow(65, false, true)]
    [DataRow(64, true, true)]
    [Timeout(30000, CooperativeCancellation = true)]
    public async Task QueuedInspectionDrainsAfterEvaluationCancellation(int queuedCount, bool cancelMiddle, bool waitForDeadline)
    {
        string waitPath = CreateResultsViewSignalPath();
        try
        {
            DapTestClient client = await StartProxyFixtureAsync(waitPath).ConfigureAwait(false);
            await using ConfiguredAsyncDisposable disposal = client.ConfigureAwait(false);
            using DapTestCancellationCapture protocolCapture = CaptureProtocolOnCancellation(client);
            JsonElement frame = await GetFixtureFrameAsync(client).ConfigureAwait(false);
            int evaluationSequence = await client.SendRequestAsync("evaluate", writer =>
            {
                writer.WriteStartObject();
                writer.WriteString("expression", "localObject.WaitForDebuggerAbortRelease()");
                writer.WriteNumber("frameId", frame.GetProperty("id").GetInt32());
                writer.WriteEndObject();
            }, TestContext.CancellationToken).ConfigureAwait(false);
            await client.WaitForTargetSignalAsync(waitPath + ".evaluation", evaluationSequence,
                TestContext.CancellationToken).ConfigureAwait(false);
            Queue<int> sequences = new();
            int initialRequests = waitForDeadline ? Math.Min(queuedCount, 64) : queuedCount;
            for (int index = 0; index < initialRequests; index++)
            {
                int sequence = await client.SendRequestAsync("threads", WriteEmptyObject,
                    TestContext.CancellationToken).ConfigureAwait(false);
                if (index < 64)
                {
                    sequences.Enqueue(sequence);
                }
                else
                {
                    using JsonDocument overflow = await client.ReadMessageAsync(TestContext.CancellationToken)
                        .ConfigureAwait(false);
                    AssertResponse(overflow.RootElement, sequence, "threads", success: false);
                    Assert.Contains("pending request limit", overflow.RootElement
                        .GetProperty("message").GetString()!, StringComparison.OrdinalIgnoreCase);
                }
            }

            if (cancelMiddle)
            {
                int removed = sequences.ElementAt(queuedCount / 2);
                await AssertQueuedRequestCanceledAsync(client, removed, "threads").ConfigureAwait(false);
                sequences = new Queue<int>(sequences.Where(sequence => sequence != removed));
                if (!waitForDeadline)
                {
                    sequences.Enqueue(await client.SendRequestAsync("threads", WriteEmptyObject,
                        TestContext.CancellationToken).ConfigureAwait(false));
                }
            }

            if (waitForDeadline)
            {
                // Prepare the full queue before the bounded abort grace; exercise admission or overflow during abort.
                await client.WaitForTargetSignalAsync(waitPath + ".evaluation.aborting", evaluationSequence,
                    TestContext.CancellationToken).ConfigureAwait(false);
                int sequence = await client.SendRequestAsync("threads", WriteEmptyObject,
                    TestContext.CancellationToken).ConfigureAwait(false);
                if (cancelMiddle)
                {
                    sequences.Enqueue(sequence);
                }
                else
                {
                    using JsonDocument overflow = await client.ReadMessageAsync(TestContext.CancellationToken)
                        .ConfigureAwait(false);
                    AssertResponse(overflow.RootElement, sequence, "threads", success: false);
                    Assert.Contains("pending request limit", overflow.RootElement
                        .GetProperty("message").GetString()!, StringComparison.OrdinalIgnoreCase);
                }
            }
            Assert.HasCount(Math.Min(queuedCount, 64), sequences);

            int cancelSequence = await SendRequestCancellationAsync(client, evaluationSequence)
                .ConfigureAwait(false);
            await AssertCanceledTargetCodeOperationAsync(client, evaluationSequence, cancelSequence, "evaluate",
                cancellationAcknowledged: () => File.WriteAllTextAsync(waitPath + ".evaluation.release", "release",
                    TestContext.CancellationToken))
                .ConfigureAwait(false);
            while (sequences.TryDequeue(out int sequence))
            {
                using JsonDocument response = await client.ReadMessageAsync(TestContext.CancellationToken)
                    .ConfigureAwait(false);
                AssertResponse(response.RootElement, sequence, "threads", success: true);
                Assert.IsNotEmpty(response.RootElement.GetProperty("body").GetProperty("threads")
                    .EnumerateArray());
            }

            JsonElement currentFrame = await GetFixtureFrameAsync(client).ConfigureAwait(false);
            JsonElement number = await ReadEvaluationAsync(client, currentFrame.GetProperty("id").GetInt32(),
                "localNumber", success: true, TestContext.CancellationToken).ConfigureAwait(false);
            Assert.AreEqual("43", number.GetProperty("result").GetString());
            await FinishResultsViewSessionAsync(client).ConfigureAwait(false);
        }
        finally
        {
            File.Delete(waitPath);
            File.Delete(waitPath + ".evaluation");
            File.Delete(waitPath + ".evaluation.aborting");
            File.Delete(waitPath + ".evaluation.release");
        }
    }

    /// <summary>
    /// Disconnects queued work even while the protocol reader is waiting for an incomplete later payload.
    /// </summary>
    [TestMethod]
    [Timeout(30000, CooperativeCancellation = true)]
    public async Task QueuedDisconnectCancelsIncompleteReadAhead()
    {
        string waitPath = CreateResultsViewSignalPath();
        try
        {
            DapTestClient client = await StartProxyFixtureAsync(waitPath).ConfigureAwait(false);
            await using ConfiguredAsyncDisposable disposal = client.ConfigureAwait(false);
            JsonElement frame = await GetFixtureFrameAsync(client).ConfigureAwait(false);
            int evaluationSequence = await client.SendRequestAsync("evaluate", writer =>
            {
                writer.WriteStartObject();
                writer.WriteString("expression", "localObject.WaitForDebuggerRelease()");
                writer.WriteNumber("frameId", frame.GetProperty("id").GetInt32());
                writer.WriteEndObject();
            }, TestContext.CancellationToken).ConfigureAwait(false);
            await client.WaitForTargetSignalAsync(waitPath + ".evaluation", evaluationSequence,
                TestContext.CancellationToken).ConfigureAwait(false);
            int disconnectSequence = await client.SendRequestAsync("disconnect", writer =>
            {
                writer.WriteStartObject();
                writer.WriteBoolean("terminateDebuggee", true);
                writer.WriteEndObject();
            }, TestContext.CancellationToken).ConfigureAwait(false);
            byte[] partialFrame = new byte[512 * 1024];
            partialFrame.AsSpan().Fill((byte)' ');
            Encoding.ASCII.GetBytes("Content-Length: 1048576\r\n\r\n", partialFrame);
            await client.SendFrameAsync(partialFrame, fragment: false, TestContext.CancellationToken)
                .ConfigureAwait(false);
            await File.WriteAllTextAsync(waitPath + ".evaluation.release", "release",
                TestContext.CancellationToken).ConfigureAwait(false);
            bool evaluated = false;
            bool disconnected = false;
            bool terminated = false;
            while (!evaluated || !disconnected || !terminated)
            {
                using JsonDocument response = await client.ReadMessageAsync(TestContext.CancellationToken)
                    .ConfigureAwait(false);
                JsonElement root = response.RootElement;
                if (root.GetProperty("type").GetString() == "event")
                {
                    string? eventName = root.GetProperty("event").GetString();
                    Assert.IsTrue(eventName is "invalidated" or "exited" or "terminated", eventName);
                    terminated |= eventName == "terminated";
                }
                else if (root.GetProperty("request_seq").GetInt32() == evaluationSequence)
                {
                    AssertResponse(root, evaluationSequence, "evaluate", success: true);
                    Assert.AreEqual("42", root.GetProperty("body").GetProperty("result").GetString());
                    evaluated = true;
                }
                else
                {
                    Assert.IsTrue(evaluated);
                    AssertResponse(root, disconnectSequence, "disconnect", success: true);
                    disconnected = true;
                }
            }

            Assert.AreEqual(0, await client.WaitForExitAsync(TestContext.CancellationToken).ConfigureAwait(false));
            Assert.AreEqual(string.Empty, client.Diagnostics.ToString());
        }
        finally
        {
            File.Delete(waitPath);
            File.Delete(waitPath + ".evaluation");
            File.Delete(waitPath + ".evaluation.release");
        }
    }

}
