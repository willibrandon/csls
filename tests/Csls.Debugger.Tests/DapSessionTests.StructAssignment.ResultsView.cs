using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Csls.Debugger.Tests;

/// <summary>
/// Verifies copied enumerable values retain exact destination storage through lazy inspection.
/// </summary>
public sealed partial class DapSessionTests
{
    /// <summary>
    /// Reuses the returned assignment snapshot when locals, fields, and elements are rediscovered.
    /// </summary>
    /// <param name="destination">The independent enumerable storage receiving the whole copy.</param>
    [TestMethod]
    [DataRow("localResultsViewStruct")]
    [DataRow("localStructContainer.Value")]
    [DataRow("localStructArray[0]")]
    [Timeout(30000, CooperativeCancellation = true)]
    public async Task StructAssignmentResultReusesResultsViewSnapshot(string destination)
    {
        string waitPath = CreateResultsViewSignalPath();
        try
        {
            DapTestClient client = await StartProxyFixtureAsync(waitPath).ConfigureAwait(false);
            await using ConfiguredAsyncDisposable disposal = client.ConfigureAwait(false);
            JsonElement frame = await GetFixtureFrameAsync(client).ConfigureAwait(false);
            int frameId = frame.GetProperty("id").GetInt32();
            await SeedAssignedStructItemsAsync(client, frameId, destination, 141, 142).ConfigureAwait(false);
            await AssertAssignedStructStorageAsync(client, frameId, destination, 141, 142, 0)
                .ConfigureAwait(false);
            await AssertAssignedStructStorageAsync(client, frameId, "localStructSource", 151, 152, 0)
                .ConfigureAwait(false);

            JsonElement assignment = await ReadSetExpressionAsync(
                client, frameId, destination, "localStructSource", success: true,
                TestContext.CancellationToken).ConfigureAwait(false);
            int assignmentReference = AssertAssignedStructReference(assignment);
            await AssertAssignedStructStorageAsync(client, frameId, destination, 151, 152, 0)
                .ConfigureAwait(false);
            await AssertAssignedStructStorageAsync(client, frameId, "localStructSource", 151, 152, 0)
                .ConfigureAwait(false);
            JsonElement lazy = await ReadAssignedStructResultsRowAsync(client, assignmentReference)
                .ConfigureAwait(false);
            AssertAssignedStructLazyRow(lazy);
            JsonElement snapshot = await ResolveResultsViewSnapshotAsync(
                client, lazy.GetProperty("variablesReference").GetInt32()).ConfigureAwait(false);
            int snapshotReference = await AssertAssignedStructSnapshotAsync(client, snapshot)
                .ConfigureAwait(false);

            await AssertAssignedStructSnapshotReuseAsync(client, frameId, destination, snapshotReference)
                .ConfigureAwait(false);
            await AssertAssignedStructStorageAsync(client, frameId, destination, 151, 152, 1)
                .ConfigureAwait(false);
            await AssertAssignedStructStorageAsync(client, frameId, "localStructSource", 151, 152, 1)
                .ConfigureAwait(false);
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
    /// Keeps a returned element bound to its original heap array after its source reference slot changes.
    /// </summary>
    [TestMethod]
    [Timeout(30000, CooperativeCancellation = true)]
    public async Task StructAssignmentOriginSurvivesArraySlotReplacement()
    {
        string waitPath = CreateResultsViewSignalPath();
        try
        {
            DapTestClient client = await StartProxyFixtureAsync(waitPath).ConfigureAwait(false);
            await using ConfiguredAsyncDisposable disposal = client.ConfigureAwait(false);
            JsonElement frame = await GetFixtureFrameAsync(client).ConfigureAwait(false);
            int frameId = frame.GetProperty("id").GetInt32();
            await SeedAssignedStructItemsAsync(client, frameId, "localStructArray[0]", 141, 142)
                .ConfigureAwait(false);
            await SeedAssignedStructItemsAsync(client, frameId, "localOtherStructArray[0]", 251, 252)
                .ConfigureAwait(false);
            await AssertAssignedStructStorageAsync(client, frameId, "localOriginalStructArray[0]", 141, 142, 0)
                .ConfigureAwait(false);
            await AssertAssignedStructStorageAsync(client, frameId, "localOtherStructArray[0]", 251, 252, 0)
                .ConfigureAwait(false);
            await AssertAssignedStructStorageAsync(client, frameId, "localStructSource", 151, 152, 0)
                .ConfigureAwait(false);

            JsonElement assignment = await ReadSetExpressionAsync(
                client, frameId, "localStructArray[0]", "localStructSource", success: true,
                TestContext.CancellationToken).ConfigureAwait(false);
            int assignmentReference = AssertAssignedStructReference(assignment);
            await AssertAssignedStructStorageAsync(client, frameId, "localOriginalStructArray[0]", 151, 152, 0)
                .ConfigureAwait(false);
            JsonElement replacement = await ReadSetExpressionAsync(
                client, frameId, "localStructArray", "localOtherStructArray", success: true,
                TestContext.CancellationToken).ConfigureAwait(false);
            Assert.AreEqual("Csls.TestProcessHost.ResultsViewStructFixture[]",
                replacement.GetProperty("type").GetString());
            Assert.IsGreaterThan(0, replacement.GetProperty("variablesReference").GetInt32());
            await AssertAssignedStructStorageAsync(client, frameId, "localStructArray[0]", 251, 252, 0)
                .ConfigureAwait(false);
            await AssertAssignedStructStorageAsync(client, frameId, "localOriginalStructArray[0]", 151, 152, 0)
                .ConfigureAwait(false);

            JsonElement lazy = await ReadAssignedStructResultsRowAsync(client, assignmentReference)
                .ConfigureAwait(false);
            AssertAssignedStructLazyRow(lazy);
            JsonElement snapshot = await ResolveResultsViewSnapshotAsync(
                client, lazy.GetProperty("variablesReference").GetInt32()).ConfigureAwait(false);
            int snapshotReference = await AssertAssignedStructSnapshotAsync(client, snapshot)
                .ConfigureAwait(false);
            await AssertAssignedStructSnapshotReuseAsync(
                client, frameId, "localOriginalStructArray[0]", snapshotReference).ConfigureAwait(false);

            JsonElement other = await ReadEvaluationAsync(
                client, frameId, "localOtherStructArray[0]", success: true, TestContext.CancellationToken)
                .ConfigureAwait(false);
            JsonElement otherRow = await ReadAssignedStructResultsRowAsync(
                client, other.GetProperty("variablesReference").GetInt32()).ConfigureAwait(false);
            AssertAssignedStructLazyRow(otherRow);
            Assert.AreNotEqual(snapshotReference, otherRow.GetProperty("variablesReference").GetInt32());
            await AssertAssignedStructStorageAsync(client, frameId, "localOtherStructArray[0]", 251, 252, 0)
                .ConfigureAwait(false);
            await AssertAssignedStructStorageAsync(client, frameId, "localOriginalStructArray[0]", 151, 152, 1)
                .ConfigureAwait(false);
            await AssertAssignedStructStorageAsync(client, frameId, "localStructSource", 151, 152, 1)
                .ConfigureAwait(false);
            await FinishResultsViewSessionAsync(client).ConfigureAwait(false);
        }
        finally
        {
            File.Delete(waitPath);
        }
    }

    /// <summary>
    /// Captures a changing array index before copying and exposes usable canonical child expressions.
    /// </summary>
    [TestMethod]
    [Timeout(30000, CooperativeCancellation = true)]
    public async Task StructAssignmentCapturesDynamicArrayIndex()
    {
        const string Destination = "localStructArray[localStructArray[0]._state._items[0]]";
        string waitPath = CreateResultsViewSignalPath();
        try
        {
            DapTestClient client = await StartProxyFixtureAsync(waitPath).ConfigureAwait(false);
            await using ConfiguredAsyncDisposable disposal = client.ConfigureAwait(false);
            JsonElement frame = await GetFixtureFrameAsync(client).ConfigureAwait(false);
            int frameId = frame.GetProperty("id").GetInt32();
            await SeedAssignedStructItemsAsync(client, frameId, "localStructArray[0]", 0, 142)
                .ConfigureAwait(false);
            await AssertAssignedStructStorageAsync(client, frameId, Destination, 0, 142, 0)
                .ConfigureAwait(false);
            await AssertAssignedStructStorageAsync(client, frameId, "localStructSource", 151, 152, 0)
                .ConfigureAwait(false);

            JsonElement assignment = await ReadSetExpressionAsync(
                client, frameId, Destination, "localStructSource", success: true,
                TestContext.CancellationToken).ConfigureAwait(false);
            int assignmentReference = AssertAssignedStructReference(assignment);
            JsonElement[] children = await ReadVariablesAsync(client, assignmentReference).ConfigureAwait(false);
            JsonElement state = Assert.ContainsSingle(children.Where(child =>
                child.GetProperty("name").GetString() == "_state"));
            string? stateExpression = state.GetProperty("evaluateName").GetString();
            Assert.AreEqual("localStructArray[0]._state", stateExpression);
            Assert.IsNotNull(stateExpression);
            JsonElement evaluatedState = await ReadEvaluationAsync(
                client, frameId, stateExpression, success: true, TestContext.CancellationToken)
                .ConfigureAwait(false);
            Assert.AreEqual("Csls.TestProcessHost.ResultsViewFixture<int>",
                evaluatedState.GetProperty("type").GetString());
            Assert.IsGreaterThan(0, evaluatedState.GetProperty("variablesReference").GetInt32());
            await AssertAssignedStructStorageAsync(client, frameId, "localStructArray[0]", 151, 152, 0)
                .ConfigureAwait(false);

            JsonElement lazy = Assert.ContainsSingle(children.Where(child =>
                child.GetProperty("name").GetString() == "Results View"));
            AssertAssignedStructLazyRow(lazy);
            JsonElement snapshot = await ResolveResultsViewSnapshotAsync(
                client, lazy.GetProperty("variablesReference").GetInt32()).ConfigureAwait(false);
            int snapshotReference = await AssertAssignedStructSnapshotAsync(client, snapshot)
                .ConfigureAwait(false);
            await AssertAssignedStructSnapshotReuseAsync(
                client, frameId, "localStructArray[0]", snapshotReference).ConfigureAwait(false);
            await AssertAssignedStructStorageAsync(client, frameId, "localStructArray[0]", 151, 152, 1)
                .ConfigureAwait(false);
            await AssertAssignedStructStorageAsync(client, frameId, "localStructSource", 151, 152, 1)
                .ConfigureAwait(false);
            await FinishResultsViewSessionAsync(client).ConfigureAwait(false);
        }
        finally
        {
            File.Delete(waitPath);
        }
    }

    private static int AssertAssignedStructReference(JsonElement assignment)
    {
        Assert.AreEqual("Csls.TestProcessHost.ResultsViewStructFixture", assignment.GetProperty("type").GetString());
        Assert.AreEqual("{Csls.TestProcessHost.ResultsViewStructFixture}", assignment.GetProperty("value").GetString());
        int reference = assignment.GetProperty("variablesReference").GetInt32();
        Assert.IsGreaterThan(0, reference);
        return reference;
    }

    private async Task SeedAssignedStructItemsAsync(
        DapTestClient client, int frameId, string expression, int first, int second)
    {
        int[] values = [first, second];
        for (int index = 0; index < values.Length; index++)
        {
            string expected = values[index].ToString(CultureInfo.InvariantCulture);
            JsonElement assignment = await ReadSetExpressionAsync(
                client, frameId, $"{expression}._state._items[{index}]", expected,
                success: true, TestContext.CancellationToken).ConfigureAwait(false);
            Assert.AreEqual(expected, assignment.GetProperty("value").GetString());
            Assert.AreEqual("int", assignment.GetProperty("type").GetString());
            Assert.AreEqual(0, assignment.GetProperty("variablesReference").GetInt32());
        }
    }

    private async Task AssertAssignedStructStorageAsync(
        DapTestClient client, int frameId, string expression, int first, int second, int enumerationCount)
    {
        JsonElement items = await ReadEvaluationAsync(
            client, frameId, expression + "._state._items", success: true, TestContext.CancellationToken)
            .ConfigureAwait(false);
        Assert.AreEqual("int[]", items.GetProperty("type").GetString());
        Assert.AreEqual("{int[2]}", items.GetProperty("result").GetString());
        JsonElement[] values = await ReadVariablesAsync(client, items.GetProperty("variablesReference").GetInt32())
            .ConfigureAwait(false);
        Assert.AreSequenceEqual(
            [first.ToString(CultureInfo.InvariantCulture), second.ToString(CultureInfo.InvariantCulture)],
            values.Select(value => value.GetProperty("value").GetString()).ToArray());
        Assert.AreSequenceEqual(["[0]", "[1]"], values.Select(value => value.GetProperty("name").GetString()).ToArray());
        foreach (JsonElement value in values)
        {
            Assert.AreEqual("int", value.GetProperty("type").GetString());
            Assert.AreEqual(0, value.GetProperty("variablesReference").GetInt32());
        }

        JsonElement count = await ReadEvaluationAsync(
            client, frameId, expression + "._state._enumerationCount", success: true, TestContext.CancellationToken)
            .ConfigureAwait(false);
        Assert.AreEqual(enumerationCount.ToString(CultureInfo.InvariantCulture), count.GetProperty("result").GetString());
        Assert.AreEqual("int", count.GetProperty("type").GetString());
        Assert.AreEqual(0, count.GetProperty("variablesReference").GetInt32());
    }

    private async Task<JsonElement> ReadAssignedStructResultsRowAsync(DapTestClient client, int reference)
    {
        JsonElement[] children = await ReadVariablesAsync(client, reference).ConfigureAwait(false);
        return Assert.ContainsSingle(children.Where(child => child.GetProperty("name").GetString() == "Results View"));
    }

    private static void AssertAssignedStructLazyRow(JsonElement row)
    {
        Assert.AreEqual("Expanding the Results View will enumerate the IEnumerable", row.GetProperty("value").GetString());
        Assert.IsGreaterThan(0, row.GetProperty("variablesReference").GetInt32());
        Assert.IsFalse(row.TryGetProperty("evaluateName", out _));
        JsonElement hint = row.GetProperty("presentationHint");
        Assert.AreEqual("virtual", hint.GetProperty("kind").GetString());
        Assert.IsTrue(hint.GetProperty("lazy").GetBoolean());
        Assert.AreSequenceEqual(["readOnly", "hasSideEffects"],
            hint.GetProperty("attributes").EnumerateArray().Select(attribute => attribute.GetString()).ToArray());
    }

    private async Task<int> AssertAssignedStructSnapshotAsync(DapTestClient client, JsonElement snapshot)
    {
        int reference = snapshot.GetProperty("variablesReference").GetInt32();
        Assert.IsGreaterThan(0, reference);
        Assert.AreEqual(2, snapshot.GetProperty("indexedVariables").GetInt32());
        Assert.AreEqual(0, snapshot.GetProperty("namedVariables").GetInt32());
        JsonElement[] values = await ReadVariablesAsync(client, reference).ConfigureAwait(false);
        Assert.AreSequenceEqual(["151", "152"], values.Select(value => value.GetProperty("value").GetString()).ToArray());
        Assert.AreSequenceEqual(["[0]", "[1]"], values.Select(value => value.GetProperty("name").GetString()).ToArray());
        foreach (JsonElement value in values)
        {
            Assert.AreEqual("int", value.GetProperty("type").GetString());
            Assert.AreEqual(0, value.GetProperty("variablesReference").GetInt32());
            Assert.IsFalse(value.TryGetProperty("evaluateName", out _));
        }

        return reference;
    }

    private async Task AssertAssignedStructSnapshotReuseAsync(
        DapTestClient client, int frameId, string expression, int snapshotReference)
    {
        JsonElement value = await ReadEvaluationAsync(
            client, frameId, expression, success: true, TestContext.CancellationToken).ConfigureAwait(false);
        JsonElement row = await ReadAssignedStructResultsRowAsync(client, value.GetProperty("variablesReference").GetInt32())
            .ConfigureAwait(false);
        Assert.AreEqual(snapshotReference, row.GetProperty("variablesReference").GetInt32());
        Assert.IsFalse(row.TryGetProperty("evaluateName", out _));
        JsonElement hint = row.GetProperty("presentationHint");
        Assert.AreEqual("virtual", hint.GetProperty("kind").GetString());
        Assert.IsFalse(hint.TryGetProperty("lazy", out JsonElement lazy) && lazy.GetBoolean());
        Assert.AreSequenceEqual(["readOnly"],
            hint.GetProperty("attributes").EnumerateArray().Select(attribute => attribute.GetString()).ToArray());
        Assert.AreEqual(snapshotReference, await AssertAssignedStructSnapshotAsync(client, row).ConfigureAwait(false));
    }
}
