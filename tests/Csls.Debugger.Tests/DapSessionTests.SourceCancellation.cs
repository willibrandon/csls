using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Csls.Debugger.Tests;

/// <summary>
/// Verifies cancellation while a real Source Link response is waiting for its body.
/// </summary>
public sealed partial class DapSessionTests
{
    /// <summary>
    /// Cancels blocked source retrieval and queued inspection without changing the stopped target.
    /// </summary>
    [TestMethod]
    [Timeout(30000, CooperativeCancellation = true)]
    public async Task SourceDownloadCancellationPreservesQueuedInspectionAndRetry()
    {
        string directory = Path.Join(Path.GetTempPath(), $"csls-source-cancellation-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            SourceLinkTestServer server = SymbolFixtures.CancellationSourceLinkServer;
            DapTestClient client = await DapTestClient.CreateAsync(TestContext.CancellationToken).ConfigureAwait(false);
            await using ConfiguredAsyncDisposable disposal = client.ConfigureAwait(false);
            int threadId = await StartSourceCancellationFixtureAsync(client, server, directory).ConfigureAwait(false);
            int sourceReference = await ReadSourceLinkReferenceAsync(client, threadId).ConfigureAwait(false);
            JsonElement originalFrame = (await ReadDeepStackPageAsync(client, threadId, 0, 1).ConfigureAwait(false))
                .GetProperty("stackFrames")[0];
            int sourceSequence = await client.SendRequestAsync("source",
                writer => WriteSourceArguments(writer, sourceReference), TestContext.CancellationToken).ConfigureAwait(false);
            await server.WaitForFirstRequestAsync(TestContext.CancellationToken).ConfigureAwait(false);
            Assert.AreEqual(1, server.RequestCount);
            TestContext.WriteLine("The source response headers arrived; its body is held open until client cancellation.");

            int canceledStack = await SendDeepStackRequestAsync(client, threadId, 0, 1).ConfigureAwait(false);
            await AssertQueuedRequestCanceledAsync(client, canceledStack, "stackTrace").ConfigureAwait(false);
            int threadsSequence = await client.SendRequestAsync("threads", WriteEmptyObject,
                TestContext.CancellationToken).ConfigureAwait(false);
            int cancelSequence = await SendRequestCancellationAsync(client, sourceSequence).ConfigureAwait(false);
            var pending = new HashSet<int> { sourceSequence, cancelSequence, threadsSequence };
            while (pending.Count > 0)
            {
                using JsonDocument response = await client.ReadMessageAsync(TestContext.CancellationToken).ConfigureAwait(false);
                JsonElement root = response.RootElement;
                int sequence = root.GetProperty("request_seq").GetInt32();
                Assert.IsTrue(pending.Remove(sequence), $"Unexpected or duplicate response: {root}");
                if (sequence == sourceSequence)
                {
                    AssertResponse(root, sourceSequence, "source", success: false);
                    Assert.AreEqual("cancelled", root.GetProperty("message").GetString());
                }
                else if (sequence == cancelSequence)
                {
                    AssertResponse(root, cancelSequence, "cancel", success: true);
                }
                else
                {
                    AssertResponse(root, threadsSequence, "threads", success: true);
                    Assert.Contains(threadId, root.GetProperty("body").GetProperty("threads")
                        .EnumerateArray().Select(static thread => thread.GetProperty("id").GetInt32()));
                }
            }

            await server.WaitForFirstDisconnectAsync(TestContext.CancellationToken).ConfigureAwait(false);
            JsonElement currentFrame = (await ReadDeepStackPageAsync(client, threadId, 0, 1).ConfigureAwait(false))
                .GetProperty("stackFrames")[0];
            Assert.AreEqual(originalFrame.GetProperty("id").GetInt32(), currentFrame.GetProperty("id").GetInt32());
            Assert.AreEqual(originalFrame.GetProperty("name").GetString(), currentFrame.GetProperty("name").GetString());
            Assert.AreEqual(originalFrame.GetProperty("line").GetInt32(), currentFrame.GetProperty("line").GetInt32());
            Assert.AreEqual(originalFrame.GetProperty("column").GetInt32(), currentFrame.GetProperty("column").GetInt32());
            Assert.AreEqual(sourceReference, currentFrame.GetProperty("source").GetProperty("sourceReference").GetInt32());
            Assert.AreEqual(originalFrame.GetProperty("instructionPointerReference").GetString(),
                currentFrame.GetProperty("instructionPointerReference").GetString());
            JsonElement value = await ReadEvaluationAsync(client, currentFrame.GetProperty("id").GetInt32(),
                "answer", success: true, TestContext.CancellationToken).ConfigureAwait(false);
            Assert.AreEqual("41", value.GetProperty("result").GetString());
            int retrySequence = await client.SendRequestAsync("source",
                writer => WriteSourceArguments(writer, sourceReference), TestContext.CancellationToken).ConfigureAwait(false);
            using (JsonDocument source = await client.ReadMessageAsync(TestContext.CancellationToken).ConfigureAwait(false))
            {
                AssertResponse(source.RootElement, retrySequence, "source", success: true);
                Assert.AreEqual(await File.ReadAllTextAsync(SymbolFixtures.SourcePath, TestContext.CancellationToken)
                    .ConfigureAwait(false), source.RootElement.GetProperty("body").GetProperty("content").GetString());
            }

            Assert.AreEqual(2, server.RequestCount);
            await AssertSourceLinkContentAsync(client, sourceReference).ConfigureAwait(false);
            Assert.AreEqual(2, server.RequestCount, "A canceled download must not poison the verified source cache.");
            await DisconnectStoppedSessionAsync(client).ConfigureAwait(false);
            Assert.AreEqual(0, await client.WaitForExitAsync(TestContext.CancellationToken).ConfigureAwait(false));
            Assert.AreEqual(string.Empty, client.Diagnostics.ToString());
        }
        finally
        {
            await DebuggerTestDirectoryReleaseWaiter.DeleteAsync(directory, TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        }
    }

    private async Task<int> StartSourceCancellationFixtureAsync(
        DapTestClient client,
        SourceLinkTestServer server,
        string directory)
    {
        int line = FindSourceLine(await File.ReadAllLinesAsync(SymbolFixtures.SourcePath, TestContext.CancellationToken)
            .ConfigureAwait(false), "answer++;");
        int initializeSequence = await client.SendRequestAsync("initialize", WriteEmptyObject,
            TestContext.CancellationToken).ConfigureAwait(false);
        using (JsonDocument initialize = await client.ReadMessageAsync(TestContext.CancellationToken).ConfigureAwait(false))
        {
            AssertResponse(initialize.RootElement, initializeSequence, "initialize", success: true);
        }

        int launchSequence = await client.SendRequestAsync("launch", writer => WriteSourceLinkLaunchArguments(writer,
            SymbolFixtures.CancellationSourceLinkProgramPath,
            [Path.Join(directory, "continue.signal"), "41", "source-link"], server.SourceLinkPattern),
            TestContext.CancellationToken).ConfigureAwait(false);
        using (JsonDocument initialized = await client.ReadMessageAsync(TestContext.CancellationToken).ConfigureAwait(false))
        {
            AssertEvent(initialized.RootElement, "initialized");
        }

        int breakpointSequence = await client.SendRequestAsync("setBreakpoints",
            writer => WriteSourceBreakpointArguments(writer, "/_/SourceLink/Program.cs", line),
            TestContext.CancellationToken).ConfigureAwait(false);
        using (JsonDocument breakpoint = await client.ReadMessageAsync(TestContext.CancellationToken).ConfigureAwait(false))
        {
            AssertResponse(breakpoint.RootElement, breakpointSequence, "setBreakpoints", success: true);
        }

        int configurationSequence = await client.SendRequestAsync("configurationDone", WriteEmptyObject,
            TestContext.CancellationToken).ConfigureAwait(false);
        return await ReadInitialBreakpointStopAsync(client, configurationSequence, launchSequence,
            TestContext.CancellationToken).ConfigureAwait(false);
    }
}
