using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Csls.Debugger.Tests;

/// <summary>
/// Verifies invalidation events are emitted only for clients that explicitly negotiate them.
/// </summary>
public sealed partial class DapSessionTests
{
    /// <summary>
    /// Negotiates invalidation across direct writes, target-code evaluation, and lazy presentation.
    /// </summary>
    /// <param name="supportsInvalidatedEvent">The explicit client capability, or null when omitted.</param>
    [TestMethod]
    [DataRow(null)]
    [DataRow(false)]
    [DataRow(true)]
    [Timeout(30000, CooperativeCancellation = true)]
    public async Task InvalidatedEventsRequireClientCapability(bool? supportsInvalidatedEvent)
    {
        string waitPath = CreateResultsViewSignalPath();
        try
        {
            DapTestClient client = await StartProxyFixtureAsync(
                waitPath, supportsInvalidatedEvent: supportsInvalidatedEvent).ConfigureAwait(false);
            await using ConfiguredAsyncDisposable disposal = client.ConfigureAwait(false);
            JsonElement frame = await GetFixtureFrameAsync(client).ConfigureAwait(false);
            int frameId = frame.GetProperty("id").GetInt32();
            (_, int localsReference) = await ReadFrameScopeReferencesAsync(client, frameId)
                .ConfigureAwait(false);
            int expressionSequence = await client.SendRequestAsync(
                "setExpression",
                writer => WriteNegotiatedAssignmentArguments(writer, frameId, "44"),
                TestContext.CancellationToken).ConfigureAwait(false);
            await AssertNegotiatedAssignmentResponseAsync(client, expressionSequence, "setExpression", "44")
                .ConfigureAwait(false);
            await AssertNegotiatedInvalidationAsync(
                client, supportsInvalidatedEvent, frameId, "44", ["variables"]).ConfigureAwait(false);

            int variableSequence = await client.SendRequestAsync(
                "setVariable",
                writer =>
                {
                    writer.WriteStartObject();
                    writer.WriteNumber("variablesReference", localsReference);
                    writer.WriteString("name", "localNumber");
                    writer.WriteString("value", "45");
                    writer.WriteEndObject();
                },
                TestContext.CancellationToken).ConfigureAwait(false);
            await AssertNegotiatedAssignmentResponseAsync(client, variableSequence, "setVariable", "45")
                .ConfigureAwait(false);
            await AssertNegotiatedInvalidationAsync(
                client, supportsInvalidatedEvent, frameId, "45", ["variables"]).ConfigureAwait(false);

            int materializedSequence = await client.SendRequestAsync(
                "setExpression",
                writer => WriteNegotiatedAssignmentArguments(writer, frameId, "localObject.NextNumber()"),
                TestContext.CancellationToken).ConfigureAwait(false);
            await AssertNegotiatedAssignmentResponseAsync(client, materializedSequence, "setExpression", "43")
                .ConfigureAwait(false);
            await AssertNegotiatedInvalidationAsync(
                client, supportsInvalidatedEvent, frameId, "43", ["stacks", "variables"])
                .ConfigureAwait(false);

            JsonElement evaluation = await ReadEvaluationAsync(
                client, frameId, "localObject.AddForDebugger(10)", success: true,
                TestContext.CancellationToken).ConfigureAwait(false);
            Assert.AreEqual("52", evaluation.GetProperty("result").GetString());
            Assert.AreEqual("int", evaluation.GetProperty("type").GetString());
            Assert.AreEqual(0, evaluation.GetProperty("variablesReference").GetInt32());
            await AssertNegotiatedInvalidationAsync(
                client, supportsInvalidatedEvent, frameId, "43", ["stacks", "variables"])
                .ConfigureAwait(false);

            JsonElement lazy = await ReadResultsViewRowAsync(client, "localResultsView").ConfigureAwait(false);
            Assert.IsTrue(lazy.GetProperty("presentationHint").GetProperty("lazy").GetBoolean());
            JsonElement[] resolution = await ReadVariablesAsync(
                client, lazy.GetProperty("variablesReference").GetInt32()).ConfigureAwait(false);
            JsonElement snapshot = Assert.ContainsSingle(resolution);
            Assert.AreEqual("Results View", snapshot.GetProperty("name").GetString());
            Assert.AreEqual(3, snapshot.GetProperty("indexedVariables").GetInt32());
            Assert.AreEqual(0, snapshot.GetProperty("namedVariables").GetInt32());
            await AssertNegotiatedInvalidationAsync(
                client, supportsInvalidatedEvent, frameId, "43", ["stacks", "variables"])
                .ConfigureAwait(false);
            JsonElement[] children = await ReadVariablesAsync(
                client, snapshot.GetProperty("variablesReference").GetInt32()).ConfigureAwait(false);
            Assert.AreSequenceEqual(["[0]", "[1]", "[2]"],
                children.Select(child => child.GetProperty("name").GetString()).ToArray());
            Assert.AreSequenceEqual(["71", "72", "73"],
                children.Select(child => child.GetProperty("value").GetString()).ToArray());
            await AssertEnumerationCountAsync(client, "localResultsView", 1).ConfigureAwait(false);
            JsonElement unchangedFrame = await GetFixtureFrameAsync(client).ConfigureAwait(false);
            Assert.AreEqual(frameId, unchangedFrame.GetProperty("id").GetInt32());
            await FinishResultsViewSessionAsync(client).ConfigureAwait(false);
        }
        finally
        {
            File.Delete(waitPath);
        }
    }

    private static void WriteNegotiatedAssignmentArguments(Utf8JsonWriter writer, int frameId, string value)
    {
        writer.WriteStartObject();
        writer.WriteNumber("frameId", frameId);
        writer.WriteString("expression", "localNumber");
        writer.WriteString("value", value);
        writer.WriteEndObject();
    }

    private async Task AssertNegotiatedAssignmentResponseAsync(
        DapTestClient client, int sequence, string command, string value)
    {
        using JsonDocument response = await client.ReadMessageAsync(TestContext.CancellationToken)
            .ConfigureAwait(false);
        AssertResponse(response.RootElement, sequence, command, success: true);
        JsonElement body = response.RootElement.GetProperty("body");
        Assert.AreEqual(value, body.GetProperty("value").GetString());
        Assert.AreEqual("int", body.GetProperty("type").GetString());
        Assert.AreEqual(0, body.GetProperty("variablesReference").GetInt32());
    }

    private async Task AssertNegotiatedInvalidationAsync(
        DapTestClient client,
        bool? supportsInvalidatedEvent,
        int frameId,
        string expectedValue,
        string[] expectedAreas)
    {
        if (supportsInvalidatedEvent == true)
        {
            using JsonDocument invalidated = await client.ReadMessageAsync(TestContext.CancellationToken)
                .ConfigureAwait(false);
            AssertEvent(invalidated.RootElement, "invalidated");
            Assert.AreSequenceEqual(expectedAreas,
                invalidated.RootElement.GetProperty("body").GetProperty("areas").EnumerateArray()
                    .Select(area => area.GetString()).ToArray());
        }

        // The adapter settles the preceding operation before processing this request, so its
        // response proves that no unnegotiated or duplicate invalidation remained on the stream.
        JsonElement observed = await ReadEvaluationAsync(
            client, frameId, "localNumber", success: true, TestContext.CancellationToken).ConfigureAwait(false);
        Assert.AreEqual(expectedValue, observed.GetProperty("result").GetString());
        Assert.AreEqual("int", observed.GetProperty("type").GetString());
        Assert.AreEqual(0, observed.GetProperty("variablesReference").GetInt32());
    }
}
