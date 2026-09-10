using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Csls.Debugger.Tests;

/// <summary>
/// Verifies that rejected stack pages cannot retain unpublished frame and instruction bindings.
/// </summary>
public sealed partial class DapSessionTests
{
    /// <summary>
    /// Rolls back a partially registered page at the exact retained-frame budget and keeps prior pages usable.
    /// </summary>
    [TestMethod]
    [TestCategory("DebuggerStress")]
    [Timeout(180000, CooperativeCancellation = true)]
    public async Task RetainedStackFrameLimitRollsBackPartialPage()
    {
        DapTestClient client = await DapTestClient.CreateAsync(TestContext.CancellationToken).ConfigureAwait(false);
        await using ConfiguredAsyncDisposable disposal = client.ConfigureAwait(false);
        (int threadId, string sourcePath) = await StartDeepStackAsync(client, 100000).ConfigureAwait(false);
        var ids = new HashSet<int>();
        JsonElement top = default;
        for (int pageIndex = 0; pageIndex < 16; pageIndex++)
        {
            int count = pageIndex == 15 ? 4000 : 4096;
            JsonElement page = await ReadDeepStackPageAsync(client, threadId, pageIndex * 4096, count).ConfigureAwait(false);
            JsonElement frames = page.GetProperty("stackFrames");
            Assert.AreEqual(count, frames.GetArrayLength());
            if (pageIndex == 0)
            {
                top = frames[0];
            }

            foreach (JsonElement frame in frames.EnumerateArray())
            {
                Assert.IsTrue(ids.Add(frame.GetProperty("id").GetInt32()),
                    "Distinct physical activations must not share a logical frame identifier.");
            }
        }

        Assert.HasCount(65440, ids);
        int rejectedSequence = await SendDeepStackRequestAsync(client, threadId, 90000, 97).ConfigureAwait(false);
        using (JsonDocument rejected = await client.ReadMessageAsync(TestContext.CancellationToken).ConfigureAwait(false))
        {
            AssertResponse(rejected.RootElement, rejectedSequence, "stackTrace", success: false);
            Assert.Contains("retained-frame limit of 65536", rejected.RootElement.GetProperty("message").GetString()!);
        }

        JsonElement accepted = await ReadDeepStackPageAsync(client, threadId, 90000, 96).ConfigureAwait(false);
        Assert.AreEqual(96, accepted.GetProperty("stackFrames").GetArrayLength());
        foreach (JsonElement frame in accepted.GetProperty("stackFrames").EnumerateArray())
        {
            Assert.IsTrue(ids.Add(frame.GetProperty("id").GetInt32()));
        }

        Assert.HasCount(65536, ids);
        int overflowSequence = await SendDeepStackRequestAsync(client, threadId, 90096, 1).ConfigureAwait(false);
        using (JsonDocument overflow = await client.ReadMessageAsync(TestContext.CancellationToken).ConfigureAwait(false))
        {
            AssertResponse(overflow.RootElement, overflowSequence, "stackTrace", success: false);
            Assert.Contains("retained-frame limit of 65536", overflow.RootElement.GetProperty("message").GetString()!);
        }

        JsonElement topAgain = (await ReadDeepStackPageAsync(client, threadId, 0, 1).ConfigureAwait(false))
            .GetProperty("stackFrames")[0];
        AssertSameLogicalFrame(top, topAgain);
        await AssertDeepStackArgumentAsync(client, top, "entered", 100000).ConfigureAwait(false);
        await AssertDeepStackArgumentAsync(client, accepted.GetProperty("stackFrames")[95], "remaining", 90095)
            .ConfigureAwait(false);
        await FinishDeepStackAsync(client, sourcePath).ConfigureAwait(false);
    }

    /// <summary>
    /// Keeps a published logical frame reacquirable when a rejected page temporarily restored its native binding.
    /// </summary>
    [TestMethod]
    [Timeout(30000, CooperativeCancellation = true)]
    public async Task RejectedStackPagePreservesIdentitiesAfterEvaluation()
    {
        DapTestClient client = await DapTestClient.CreateAsync(TestContext.CancellationToken).ConfigureAwait(false);
        await using ConfiguredAsyncDisposable disposal = client.ConfigureAwait(false);
        (int threadId, string sourcePath) = await StartDeepStackAsync(client, 5000).ConfigureAwait(false);
        JsonElement top = (await ReadDeepStackPageAsync(client, threadId, 0, 1).ConfigureAwait(false))
            .GetProperty("stackFrames")[0];
        JsonElement deep = (await ReadDeepStackPageAsync(client, threadId, 4999, 1).ConfigureAwait(false))
            .GetProperty("stackFrames")[0];
        JsonElement evaluated = await ReadEvaluationAsync(client, top.GetProperty("id").GetInt32(),
            "Csls.TestProcessHost.DebuggerDeepStackFixture.AddOne(41)", success: true, TestContext.CancellationToken)
            .ConfigureAwait(false);
        Assert.AreEqual("42", evaluated.GetProperty("result").GetString());

        int sequence = await SendDeepStackRequestAsync(client, threadId, 0, 0).ConfigureAwait(false);
        using (JsonDocument response = await client.ReadMessageAsync(TestContext.CancellationToken).ConfigureAwait(false))
        {
            AssertResponse(response.RootElement, sequence, "stackTrace", success: false);
            Assert.Contains("frame-page limit of 4096", response.RootElement.GetProperty("message").GetString()!);
        }

        await AssertDeepStackArgumentAsync(client, top, "entered", 5000).ConfigureAwait(false);
        await AssertDeepStackArgumentAsync(client, deep, "remaining", 4999).ConfigureAwait(false);
        JsonElement refreshed = (await ReadDeepStackPageAsync(client, threadId, 0, 1).ConfigureAwait(false))
            .GetProperty("stackFrames")[0];
        AssertSameLogicalFrame(top, refreshed);
        await AssertStaleDisassemblyRejectedAsync(client, top.GetProperty("instructionPointerReference").GetString()!)
            .ConfigureAwait(false);
        await FinishDeepStackAsync(client, sourcePath).ConfigureAwait(false);
    }

    /// <summary>
    /// Rejects disjoint oversized pages beyond the retention budget while preserving published references.
    /// </summary>
    [TestMethod]
    [TestCategory("DebuggerStress")]
    [Timeout(180000, CooperativeCancellation = true)]
    public async Task RejectedStackPagesReleaseUnpublishedFrames()
    {
        DapTestClient client = await DapTestClient.CreateAsync(TestContext.CancellationToken).ConfigureAwait(false);
        await using ConfiguredAsyncDisposable disposal = client.ConfigureAwait(false);
        (int threadId, string sourcePath) = await StartDeepStackAsync(client, 100000).ConfigureAwait(false);
        JsonElement top = (await ReadDeepStackPageAsync(client, threadId, 0, 1).ConfigureAwait(false))
            .GetProperty("stackFrames")[0];
        JsonElement middle = (await ReadDeepStackPageAsync(client, threadId, 32768, 1).ConfigureAwait(false))
            .GetProperty("stackFrames")[0];
        string instructionReference = top.GetProperty("instructionPointerReference").GetString()!;
        JsonElement[] instructions = await ReadDisassemblyAsync(client, instructionReference, 0, 0, 1)
            .ConfigureAwait(false);
        Assert.HasCount(1, instructions);
        Assert.IsTrue(instructions[0].TryGetProperty("instructionBytes", out _));

        for (int start = 0; start <= 65536; start += 4096)
        {
            int sequence = await SendDeepStackRequestAsync(client, threadId, start, 0).ConfigureAwait(false);
            using JsonDocument response = await client.ReadMessageAsync(TestContext.CancellationToken).ConfigureAwait(false);
            AssertResponse(response.RootElement, sequence, "stackTrace", success: false);
            Assert.Contains("frame-page limit of 4096", response.RootElement.GetProperty("message").GetString()!,
                $"Rejected page at {start} must release unpublished frames instead of consuming the stopped-interval budget.");
            TestContext.WriteLine($"Rejected oversized stack page beginning at {start} without exhausting frame retention.");
        }

        JsonElement fresh = await ReadDeepStackPageAsync(client, threadId, 90000, 2).ConfigureAwait(false);
        Assert.AreEqual(2, fresh.GetProperty("stackFrames").GetArrayLength());
        await AssertDeepStackArgumentAsync(client, fresh.GetProperty("stackFrames")[0], "remaining", 90000)
            .ConfigureAwait(false);
        JsonElement topAgain = (await ReadDeepStackPageAsync(client, threadId, 0, 1).ConfigureAwait(false))
            .GetProperty("stackFrames")[0];
        JsonElement middleAgain = (await ReadDeepStackPageAsync(client, threadId, 32768, 1).ConfigureAwait(false))
            .GetProperty("stackFrames")[0];
        AssertSameLogicalFrame(top, topAgain);
        AssertSameLogicalFrame(middle, middleAgain);
        await AssertDeepStackArgumentAsync(client, top, "entered", 100000).ConfigureAwait(false);
        await AssertDeepStackArgumentAsync(client, middle, "remaining", 32768).ConfigureAwait(false);
        JsonElement[] opaque = await ReadDisassemblyAsync(client, instructionReference, 0, 0, 1).ConfigureAwait(false);
        JsonElement[] numeric = await ReadDisassemblyAsync(client, instructions[0].GetProperty("address").GetString()!,
            0, 0, 1).ConfigureAwait(false);
        Assert.HasCount(1, opaque);
        Assert.HasCount(1, numeric);
        Assert.AreEqual(instructions[0].GetRawText(), opaque[0].GetRawText());
        Assert.AreEqual(instructions[0].GetRawText(), numeric[0].GetRawText());
        await FinishDeepStackAsync(client, sourcePath).ConfigureAwait(false);
    }
}
