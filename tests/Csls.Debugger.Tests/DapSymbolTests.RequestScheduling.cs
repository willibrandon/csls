using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Csls.Debugger.Tests;

/// <summary>
/// Verifies queued payload accounting while real source retrieval awaits client cancellation.
/// </summary>
public sealed partial class DapSymbolTests
{
    /// <summary>
    /// Bounds retained wire payloads and releases their budget when queued work is canceled.
    /// </summary>
    [TestMethod]
    [Timeout(30000, CooperativeCancellation = true)]
    public async Task QueuedRequestPayloadBudgetIsReleasedByCancellation()
    {
        string directory = Directory.CreateTempSubdirectory("csls-source-queue-").FullName;
        try
        {
            SourceLinkTestServer server = SymbolFixtures.QueuedSourceLinkServer;
            DapTestClient client = await DapTestClient.CreateAsync(TestContext.CancellationToken).ConfigureAwait(false);
            await using ConfiguredAsyncDisposable disposal = client.ConfigureAwait(false);
            using DapTestCancellationCapture cancellationCapture = CaptureProtocolOnCancellation(client);
            int threadId = await StartSourceCancellationFixtureAsync(
                client, server, directory, SymbolFixtures.QueuedSourceLinkProgramPath).ConfigureAwait(false);
            int sourceReference = await ReadSourceLinkReferenceAsync(client, threadId).ConfigureAwait(false);
            int sourceSequence = await client.SendRequestAsync("source",
                writer => WriteSourceArguments(writer, sourceReference), TestContext.CancellationToken).ConfigureAwait(false);
            await server.WaitForFirstRequestAsync(TestContext.CancellationToken).ConfigureAwait(false);
            Assert.AreEqual(1, server.RequestCount);

            int paddedSequence = await client.SendRequestAsync("threads", WriteEmptyObject,
                TestContext.CancellationToken, minimumPayloadBytes: 16 * 1024 * 1024).ConfigureAwait(false);
            int rejectedSequence = await client.SendRequestAsync("threads", WriteEmptyObject,
                TestContext.CancellationToken).ConfigureAwait(false);
            using (JsonDocument rejected = await client.ReadMessageAsync(TestContext.CancellationToken).ConfigureAwait(false))
            {
                AssertResponse(rejected.RootElement, rejectedSequence, "threads", success: false);
                Assert.Contains("pending request limit", rejected.RootElement.GetProperty("message").GetString()!,
                    StringComparison.OrdinalIgnoreCase);
            }

            await AssertQueuedRequestCanceledAsync(client, paddedSequence, "threads").ConfigureAwait(false);
            int replacementSequence = await client.SendRequestAsync("threads", WriteEmptyObject,
                TestContext.CancellationToken, minimumPayloadBytes: 16 * 1024 * 1024).ConfigureAwait(false);
            int cancelSequence = await SendRequestCancellationAsync(client, sourceSequence).ConfigureAwait(false);
            var pending = new HashSet<int> { sourceSequence, cancelSequence, replacementSequence };
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
                    AssertResponse(root, replacementSequence, "threads", success: true);
                    Assert.Contains(threadId, root.GetProperty("body").GetProperty("threads")
                        .EnumerateArray().Select(static thread => thread.GetProperty("id").GetInt32()));
                }
            }

            await server.WaitForFirstDisconnectAsync(TestContext.CancellationToken).ConfigureAwait(false);
            Assert.AreEqual(sourceReference, await ReadSourceLinkReferenceAsync(client, threadId).ConfigureAwait(false));
            await AssertSourceLinkContentAsync(client, sourceReference).ConfigureAwait(false);
            Assert.AreEqual(2, server.RequestCount);
            await DisconnectStoppedSessionAsync(client).ConfigureAwait(false);
            Assert.AreEqual(0, await client.WaitForExitAsync(TestContext.CancellationToken).ConfigureAwait(false));
            Assert.AreEqual(string.Empty, client.Diagnostics.ToString());
        }
        finally
        {
            await DebuggerTestDirectoryReleaseWaiter.DeleteAsync(directory, TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        }
    }
}
