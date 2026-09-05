using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Csls.Debugger.Tests;

/// <summary>
/// Verifies Results View identity across expression inspection and direct debugger writes.
/// </summary>
public sealed partial class DapSessionTests
{
    /// <summary>
    /// Shares one authorized enumeration with expression inspection of the same physical receiver.
    /// </summary>
    /// <param name="localName">The reference, value, nullable, or boxed enumerable to inspect.</param>
    /// <param name="structCounter">Whether its counter belongs to shared struct state.</param>
    /// <param name="expectedValues">The exact ordered values in the retained snapshot.</param>
    [TestMethod]
    [DataRow("localResultsView", false, new[] { "71", "72", "73" })]
    [DataRow("localResultsViewStruct", true, new[] { "151", "152" })]
    [DataRow("localResultsViewNullableStruct", true, new[] { "151", "152" })]
    [DataRow("localResultsViewBoxedStruct", true, new[] { "151", "152" })]
    [Timeout(30000, CooperativeCancellation = true)]
    public async Task ResultsViewSnapshotIsSharedWithExpressionInspection(
        string localName,
        bool structCounter,
        string[] expectedValues)
    {
        ArgumentNullException.ThrowIfNull(expectedValues);
        string waitPath = CreateResultsViewSignalPath();
        try
        {
            DapTestClient client = await StartProxyFixtureAsync(waitPath).ConfigureAwait(false);
            await using ConfiguredAsyncDisposable disposal = client.ConfigureAwait(false);
            JsonElement lazy = await ReadResultsViewRowAsync(client, localName).ConfigureAwait(false);
            JsonElement snapshot = await ResolveResultsViewSnapshotAsync(
                client, lazy.GetProperty("variablesReference").GetInt32()).ConfigureAwait(false);
            int snapshotReference = snapshot.GetProperty("variablesReference").GetInt32();
            Assert.AreEqual(expectedValues.Length, snapshot.GetProperty("indexedVariables").GetInt32());
            Assert.AreEqual(0, snapshot.GetProperty("namedVariables").GetInt32());
            JsonElement frame = await GetFixtureFrameAsync(client).ConfigureAwait(false);
            int frameId = frame.GetProperty("id").GetInt32();
            JsonElement evaluation = await ReadEvaluationAsync(
                client, frameId, localName, success: true, TestContext.CancellationToken)
                .ConfigureAwait(false);
            int evaluatedReference = evaluation.GetProperty("variablesReference").GetInt32();
            Assert.IsGreaterThan(0, evaluatedReference);
            JsonElement[] fields = await ReadVariablesAsync(client, evaluatedReference)
                .ConfigureAwait(false);
            JsonElement shared = Assert.ContainsSingle(fields.Where(field =>
                field.GetProperty("name").GetString() == "Results View"));
            Assert.AreEqual(snapshotReference, shared.GetProperty("variablesReference").GetInt32());
            Assert.AreEqual(expectedValues.Length, shared.GetProperty("indexedVariables").GetInt32());
            Assert.AreEqual(0, shared.GetProperty("namedVariables").GetInt32());
            Assert.IsFalse(shared.TryGetProperty("evaluateName", out _));
            JsonElement hint = shared.GetProperty("presentationHint");
            Assert.AreEqual("virtual", hint.GetProperty("kind").GetString());
            Assert.IsFalse(hint.TryGetProperty("lazy", out JsonElement isLazy) && isLazy.GetBoolean());
            Assert.AreSequenceEqual(
                ["readOnly"],
                hint.GetProperty("attributes").EnumerateArray()
                    .Select(attribute => attribute.GetString()).ToArray());

            JsonElement[] values = await ReadVariablesAsync(client, snapshotReference)
                .ConfigureAwait(false);
            Assert.AreSequenceEqual(
                expectedValues,
                values.Select(value => value.GetProperty("value").GetString()).ToArray());
            Assert.AreSequenceEqual(
                Enumerable.Range(0, expectedValues.Length).Select(index => $"[{index}]").ToArray(),
                values.Select(value => value.GetProperty("name").GetString()).ToArray());
            foreach (JsonElement value in values)
            {
                Assert.AreEqual("int", value.GetProperty("type").GetString());
                Assert.IsFalse(value.TryGetProperty("evaluateName", out _));
            }

            if (structCounter)
            {
                await AssertStructEnumerationCountAsync(client, localName, 1).ConfigureAwait(false);
            }
            else
            {
                await AssertEnumerationCountAsync(client, localName, 1).ConfigureAwait(false);
            }

            JsonElement unchangedFrame = await GetFixtureFrameAsync(client).ConfigureAwait(false);
            Assert.AreEqual(frameId, unchangedFrame.GetProperty("id").GetInt32());
            await FinishResultsViewSessionAsync(client).ConfigureAwait(false);
        }
        finally
        {
            File.Delete(waitPath);
        }
    }

    /// <summary>
    /// Retires snapshot descendants after a direct write without retiring ordinary stopped frames.
    /// </summary>
    [TestMethod]
    [Timeout(30000, CooperativeCancellation = true)]
    public async Task ResultsViewSnapshotRetiresAfterDirectAssignment()
    {
        string waitPath = CreateResultsViewSignalPath();
        try
        {
            DapTestClient client = await StartProxyFixtureAsync(waitPath).ConfigureAwait(false);
            await using ConfiguredAsyncDisposable disposal = client.ConfigureAwait(false);
            JsonElement lazy = await ReadResultsViewRowAsync(client, "localResultsViewInherited")
                .ConfigureAwait(false);
            JsonElement snapshot = await ResolveResultsViewSnapshotAsync(
                client, lazy.GetProperty("variablesReference").GetInt32()).ConfigureAwait(false);
            int snapshotReference = snapshot.GetProperty("variablesReference").GetInt32();
            Assert.AreEqual(2, snapshot.GetProperty("indexedVariables").GetInt32());
            Assert.AreEqual(0, snapshot.GetProperty("namedVariables").GetInt32());
            (int childReference, string memoryReference) = await AssertInheritedSnapshotValuesAsync(
                client, snapshotReference).ConfigureAwait(false);
            int readSequence = await client.SendRequestAsync(
                "readMemory", writer => WriteMemoryArguments(writer, memoryReference, 0, 1),
                TestContext.CancellationToken).ConfigureAwait(false);
            using (JsonDocument read = await client.ReadMessageAsync(TestContext.CancellationToken)
                .ConfigureAwait(false))
            {
                AssertResponse(read.RootElement, readSequence, "readMemory", success: true);
                string? data = read.RootElement.GetProperty("body").GetProperty("data").GetString();
                Assert.IsNotNull(data);
                Assert.HasCount(1, Convert.FromBase64String(data));
            }

            await AssertEnumerationCountAsync(client, "localResultsViewInherited", 1)
                .ConfigureAwait(false);
            JsonElement frame = await GetFixtureFrameAsync(client).ConfigureAwait(false);
            int frameId = frame.GetProperty("id").GetInt32();
            JsonElement ordinary = await ReadResultsViewLocalAsync(client, "localResultsViewInherited")
                .ConfigureAwait(false);
            int ordinaryReference = ordinary.GetProperty("variablesReference").GetInt32();
            Assert.IsGreaterThan(0, ordinaryReference);

            JsonElement assignment = await ReadSetExpressionAsync(
                client, frameId, "localResultsView._enumerationCount", "7", success: true,
                TestContext.CancellationToken).ConfigureAwait(false);
            Assert.AreEqual("7", assignment.GetProperty("value").GetString());
            Assert.AreEqual("int", assignment.GetProperty("type").GetString());
            Assert.AreEqual(0, assignment.GetProperty("variablesReference").GetInt32());
            int[] retiredReferences = [snapshotReference, childReference];
            foreach (int reference in retiredReferences)
            {
                int sequence = await client.SendRequestAsync(
                    "variables", writer => WriteResultsViewReference(writer, reference),
                    TestContext.CancellationToken).ConfigureAwait(false);
                using JsonDocument response = await client.ReadMessageAsync(TestContext.CancellationToken)
                    .ConfigureAwait(false);
                AssertResponse(response.RootElement, sequence, "variables", success: false);
                string? message = response.RootElement.GetProperty("message").GetString();
                Assert.IsNotNull(message);
                Assert.Contains("stale", message, StringComparison.OrdinalIgnoreCase);
            }

            int[] readCounts = [0, 1];
            foreach (int count in readCounts)
            {
                int sequence = await client.SendRequestAsync(
                    "readMemory", writer => WriteMemoryArguments(writer, memoryReference, 0, count),
                    TestContext.CancellationToken).ConfigureAwait(false);
                using JsonDocument response = await client.ReadMessageAsync(TestContext.CancellationToken)
                    .ConfigureAwait(false);
                AssertResponse(response.RootElement, sequence, "readMemory", success: false);
                string? message = response.RootElement.GetProperty("message").GetString();
                Assert.IsNotNull(message);
                Assert.Contains("stale", message, StringComparison.OrdinalIgnoreCase);
            }

            JsonElement assignedValue = await ReadEvaluationAsync(
                client, frameId, "localResultsView._enumerationCount", success: true,
                TestContext.CancellationToken).ConfigureAwait(false);
            Assert.AreEqual("7", assignedValue.GetProperty("result").GetString());
            JsonElement localNumber = await ReadEvaluationAsync(
                client, frameId, "localNumber", success: true, TestContext.CancellationToken)
                .ConfigureAwait(false);
            Assert.AreEqual("43", localNumber.GetProperty("result").GetString());
            JsonElement unchangedFrame = await GetFixtureFrameAsync(client).ConfigureAwait(false);
            Assert.AreEqual(frameId, unchangedFrame.GetProperty("id").GetInt32());
            JsonElement[] ordinaryFields = await ReadVariablesAsync(client, ordinaryReference)
                .ConfigureAwait(false);
            JsonElement freshFromOrdinary = Assert.ContainsSingle(ordinaryFields.Where(field =>
                field.GetProperty("name").GetString() == "Results View"));
            Assert.IsTrue(freshFromOrdinary.GetProperty("presentationHint").GetProperty("lazy")
                .GetBoolean());
            await AssertEnumerationCountAsync(client, "localResultsViewInherited", 1)
                .ConfigureAwait(false);
            JsonElement freshLazy = await ReadResultsViewRowAsync(client, "localResultsViewInherited")
                .ConfigureAwait(false);
            JsonElement refreshed = await ResolveResultsViewSnapshotAsync(
                client, freshLazy.GetProperty("variablesReference").GetInt32()).ConfigureAwait(false);
            Assert.AreEqual(2, refreshed.GetProperty("indexedVariables").GetInt32());
            Assert.AreEqual(0, refreshed.GetProperty("namedVariables").GetInt32());
            int refreshedReference = refreshed.GetProperty("variablesReference").GetInt32();
            Assert.AreNotEqual(snapshotReference, refreshedReference);
            _ = await AssertInheritedSnapshotValuesAsync(client, refreshedReference).ConfigureAwait(false);
            await AssertEnumerationCountAsync(client, "localResultsViewInherited", 2)
                .ConfigureAwait(false);
            await AssertEnumerationCountAsync(client, "localResultsView", 7).ConfigureAwait(false);
            await FinishResultsViewSessionAsync(client).ConfigureAwait(false);
        }
        finally
        {
            File.Delete(waitPath);
        }
    }

    /// <summary>
    /// Keeps independent struct receivers separate even when their enumerated values are identical.
    /// </summary>
    /// <param name="otherLocalName">The independent nullable or boxed struct receiver.</param>
    [TestMethod]
    [DataRow("localResultsViewNullableStruct")]
    [DataRow("localResultsViewBoxedStruct")]
    [Timeout(30000, CooperativeCancellation = true)]
    public async Task DistinctStructReceiversDoNotShareResultsViewSnapshots(string otherLocalName)
    {
        string waitPath = CreateResultsViewSignalPath();
        try
        {
            DapTestClient client = await StartProxyFixtureAsync(waitPath).ConfigureAwait(false);
            await using ConfiguredAsyncDisposable disposal = client.ConfigureAwait(false);
            JsonElement lazy = await ReadResultsViewRowAsync(client, "localResultsViewStruct")
                .ConfigureAwait(false);
            JsonElement snapshot = await ResolveResultsViewSnapshotAsync(
                client, lazy.GetProperty("variablesReference").GetInt32()).ConfigureAwait(false);
            int snapshotReference = snapshot.GetProperty("variablesReference").GetInt32();
            Assert.AreEqual(2, snapshot.GetProperty("indexedVariables").GetInt32());
            Assert.AreEqual(0, snapshot.GetProperty("namedVariables").GetInt32());
            await AssertStructSnapshotItemsAsync(client, snapshotReference).ConfigureAwait(false);
            await AssertStructEnumerationCountAsync(client, "localResultsViewStruct", 1)
                .ConfigureAwait(false);
            await AssertStructEnumerationCountAsync(client, otherLocalName, 0).ConfigureAwait(false);
            JsonElement otherLazy = await ReadResultsViewRowAsync(client, otherLocalName)
                .ConfigureAwait(false);
            Assert.AreNotEqual(snapshotReference, otherLazy.GetProperty("variablesReference").GetInt32());

            JsonElement[] refreshedFields = await ReadUnproxiedLocalAsync(client, "localResultsViewStruct")
                .ConfigureAwait(false);
            JsonElement refreshedSnapshot = Assert.ContainsSingle(refreshedFields.Where(field =>
                field.GetProperty("name").GetString() == "Results View"));
            Assert.AreEqual(snapshotReference, refreshedSnapshot.GetProperty("variablesReference").GetInt32());
            Assert.AreEqual(2, refreshedSnapshot.GetProperty("indexedVariables").GetInt32());
            Assert.AreEqual(0, refreshedSnapshot.GetProperty("namedVariables").GetInt32());
            JsonElement hint = refreshedSnapshot.GetProperty("presentationHint");
            Assert.IsFalse(hint.TryGetProperty("lazy", out JsonElement isLazy) && isLazy.GetBoolean());
            await AssertStructEnumerationCountAsync(client, "localResultsViewStruct", 1)
                .ConfigureAwait(false);
            await AssertStructEnumerationCountAsync(client, otherLocalName, 0).ConfigureAwait(false);

            JsonElement otherSnapshot = await ResolveResultsViewSnapshotAsync(
                client, otherLazy.GetProperty("variablesReference").GetInt32()).ConfigureAwait(false);
            int otherReference = otherSnapshot.GetProperty("variablesReference").GetInt32();
            Assert.AreNotEqual(snapshotReference, otherReference);
            Assert.AreEqual(2, otherSnapshot.GetProperty("indexedVariables").GetInt32());
            Assert.AreEqual(0, otherSnapshot.GetProperty("namedVariables").GetInt32());
            await AssertStructSnapshotItemsAsync(client, otherReference).ConfigureAwait(false);
            await AssertStructEnumerationCountAsync(client, "localResultsViewStruct", 1)
                .ConfigureAwait(false);
            await AssertStructEnumerationCountAsync(client, otherLocalName, 1).ConfigureAwait(false);
            await FinishResultsViewSessionAsync(client).ConfigureAwait(false);
        }
        finally
        {
            File.Delete(waitPath);
        }
    }

    private async Task AssertStructSnapshotItemsAsync(DapTestClient client, int reference)
    {
        JsonElement[] items = await ReadVariablesAsync(client, reference).ConfigureAwait(false);
        Assert.AreSequenceEqual(
            ["[0]", "[1]"],
            items.Select(item => item.GetProperty("name").GetString()).ToArray());
        Assert.AreSequenceEqual(
            ["151", "152"],
            items.Select(item => item.GetProperty("value").GetString()).ToArray());
        foreach (JsonElement item in items)
        {
            Assert.AreEqual("int", item.GetProperty("type").GetString());
            Assert.IsFalse(item.TryGetProperty("evaluateName", out _));
        }
    }

    private async Task<(int Reference, string MemoryReference)> AssertInheritedSnapshotValuesAsync(
        DapTestClient client,
        int reference)
    {
        JsonElement[] children = await ReadVariablesAsync(client, reference).ConfigureAwait(false);
        Assert.AreSequenceEqual(
            ["[0]", "[1]"],
            children.Select(child => child.GetProperty("name").GetString()).ToArray());
        string[] expectedValues = ["101", "102"];
        for (int index = 0; index < children.Length; index++)
        {
            JsonElement child = children[index];
            Assert.AreEqual("int[]", child.GetProperty("type").GetString());
            Assert.IsFalse(child.TryGetProperty("evaluateName", out _));
            int childReference = child.GetProperty("variablesReference").GetInt32();
            Assert.IsGreaterThan(0, childReference);
            JsonElement element = Assert.ContainsSingle(await ReadVariablesAsync(client, childReference)
                .ConfigureAwait(false));
            Assert.AreEqual("[0]", element.GetProperty("name").GetString());
            Assert.AreEqual("int", element.GetProperty("type").GetString());
            Assert.AreEqual(expectedValues[index], element.GetProperty("value").GetString());
            Assert.IsFalse(element.TryGetProperty("evaluateName", out _));
        }

        string? memoryReference = children[0].GetProperty("memoryReference").GetString();
        Assert.IsNotNull(memoryReference);
        Assert.StartsWith("csls-memory-", memoryReference);
        return (children[0].GetProperty("variablesReference").GetInt32(), memoryReference);
    }
}
