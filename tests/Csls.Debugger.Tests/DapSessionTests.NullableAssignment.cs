using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Csls.Debugger.Tests;

/// <summary>
/// Verifies null-literal assignment against exact stopped nullable storage.
/// </summary>
public sealed partial class DapSessionTests
{
    /// <summary>
    /// Clears present and already absent nullable locals without target execution or frame retirement.
    /// </summary>
    /// <param name="setVariable">Whether to assign through the locals container.</param>
    /// <param name="value">The direct or parenthesized null literal.</param>
    [TestMethod]
    [DataRow(false, "null")]
    [DataRow(true, "null")]
    [DataRow(false, "(null)")]
    [DataRow(true, "(null)")]
    [Timeout(30000, CooperativeCancellation = true)]
    public async Task NullableAssignmentAcceptsNullLiteral(bool setVariable, string value)
    {
        string waitPath = CreateResultsViewSignalPath();
        try
        {
            DapTestClient client = await StartProxyFixtureAsync(waitPath).ConfigureAwait(false);
            await using ConfiguredAsyncDisposable disposal = client.ConfigureAwait(false);
            JsonElement frame = await GetFixtureFrameAsync(client).ConfigureAwait(false);
            int frameId = frame.GetProperty("id").GetInt32();
            (_, int localsReference) = await ReadFrameScopeReferencesAsync(client, frameId).ConfigureAwait(false);
            (string Name, string Before)[] destinations = [("localNullable", "45"), ("localEmptyNullable", "null")];
            foreach ((string name, string before) in destinations)
            {
                await AssertStructAssignmentEvaluationAsync(client, frameId, name, before, "int?")
                    .ConfigureAwait(false);
                JsonElement assigned = setVariable
                    ? await ReadSetVariableAsync(client, localsReference, name, value, success: true,
                        TestContext.CancellationToken).ConfigureAwait(false)
                    : await ReadSetExpressionAsync(client, frameId, name, value, success: true,
                        TestContext.CancellationToken).ConfigureAwait(false);
                Assert.AreEqual("null", assigned.GetProperty("value").GetString());
                await AssertStructAssignmentNullableChildrenAsync(client, assigned, hasValue: false)
                    .ConfigureAwait(false);
                await AssertStructAssignmentGenericStorageAsync(client, frameId, name, "int", "0", nullable: true)
                    .ConfigureAwait(false);
            }

            await AssertStructAssignmentEvaluationAsync(client, frameId, "localNumber", "43", "int")
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
    /// Clears complete nullable tuple storage while preserving destination names and adjacent references.
    /// </summary>
    /// <param name="setVariable">Whether to assign through the destination container.</param>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    [Timeout(30000, CooperativeCancellation = true)]
    public async Task NullableAssignmentClearsReferenceContainingStorage(bool setVariable)
    {
        string waitPath = CreateResultsViewSignalPath();
        try
        {
            DapTestClient client = await StartNullableAssignmentFixtureAsync(waitPath).ConfigureAwait(false);
            await using ConfiguredAsyncDisposable disposal = client.ConfigureAwait(false);
            JsonElement frame = await GetFixtureFrameAsync(client).ConfigureAwait(false);
            int frameId = frame.GetProperty("id").GetInt32();
            (int argumentsReference, int localsReference) = await ReadFrameScopeReferencesAsync(client, frameId)
                .ConfigureAwait(false);
            JsonElement field = await ReadEvaluationAsync(client, frameId, "field", success: true,
                TestContext.CancellationToken).ConfigureAwait(false);
            JsonElement array = await ReadEvaluationAsync(client, frameId, "array", success: true,
                TestContext.CancellationToken).ConfigureAwait(false);
            (string Expression, int Container, string Name, string NumberName, string TextName, string Number,
                string Text)[] destinations =
            [
                ("local", localsReference, "local", "Number", "Text", "212", "\"local\""),
                ("argument", argumentsReference, "argument", "ArgumentNumber", "ArgumentText", "211", "\"argument\""),
                ("field.Value", field.GetProperty("variablesReference").GetInt32(), "Value", "Code", "Label",
                    "213", "\"field\""),
                ("array[0]", array.GetProperty("variablesReference").GetInt32(), "[0]", "Index", "Element",
                    "214", "\"array\"")
            ];
            foreach ((string Expression, int Container, string Name, string NumberName, string TextName,
                string Number, string Text) destination in destinations)
            {
                JsonElement before = await ReadEvaluationAsync(client, frameId, destination.Expression, success: true,
                    TestContext.CancellationToken).ConfigureAwait(false);
                await AssertNullableTupleStorageAsync(client, before, destination.Expression, destination.NumberName,
                    destination.TextName, hasValue: true, destination.Number, destination.Text).ConfigureAwait(false);
                JsonElement assigned = setVariable
                    ? await ReadSetVariableAsync(client, destination.Container, destination.Name, "null", success: true,
                        TestContext.CancellationToken).ConfigureAwait(false)
                    : await ReadSetExpressionAsync(client, frameId, destination.Expression, "null", success: true,
                        TestContext.CancellationToken).ConfigureAwait(false);
                Assert.AreEqual("null", assigned.GetProperty("value").GetString());
                await AssertNullableTupleStorageAsync(client, assigned, destination.Expression, destination.NumberName,
                    destination.TextName, hasValue: false, "0", "null").ConfigureAwait(false);
                JsonElement refreshed = await ReadEvaluationAsync(client, frameId, destination.Expression, success: true,
                    TestContext.CancellationToken).ConfigureAwait(false);
                Assert.AreEqual("null", refreshed.GetProperty("result").GetString());
                await AssertNullableTupleStorageAsync(client, refreshed, destination.Expression,
                    destination.NumberName, destination.TextName, hasValue: false, "0", "null").ConfigureAwait(false);
            }

            JsonElement untouched = await ReadEvaluationAsync(client, frameId, "array[1]", success: true,
                TestContext.CancellationToken).ConfigureAwait(false);
            await AssertNullableTupleStorageAsync(client, untouched, "array[1]", "Index", "Element", hasValue: true,
                "215", "\"untouched\"").ConfigureAwait(false);
            await AssertNullableGenericFieldNamesAsync(client, frameId).ConfigureAwait(false);
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
    /// Rejects reference-typed null expressions without clearing nullable destination storage.
    /// </summary>
    /// <param name="value">The source expression that is not an untyped null literal.</param>
    [TestMethod]
    [DataRow("(object)null")]
    [DataRow("(string)null")]
    [DataRow("typedNull")]
    [Timeout(30000, CooperativeCancellation = true)]
    public async Task NullableAssignmentRejectsReferenceTypedNull(string value)
    {
        string waitPath = CreateResultsViewSignalPath();
        try
        {
            DapTestClient client = await StartNullableAssignmentFixtureAsync(waitPath).ConfigureAwait(false);
            await using ConfiguredAsyncDisposable disposal = client.ConfigureAwait(false);
            JsonElement frame = await GetFixtureFrameAsync(client).ConfigureAwait(false);
            int frameId = frame.GetProperty("id").GetInt32();
            _ = await ReadSetExpressionAsync(client, frameId, "local", value, success: false,
                TestContext.CancellationToken).ConfigureAwait(false);
            JsonElement unchanged = await ReadEvaluationAsync(client, frameId, "local", success: true,
                TestContext.CancellationToken).ConfigureAwait(false);
            await AssertNullableTupleStorageAsync(client, unchanged, "local", "Number", "Text", hasValue: true,
                "212", "\"local\"").ConfigureAwait(false);
            JsonElement unchangedFrame = await GetFixtureFrameAsync(client).ConfigureAwait(false);
            Assert.AreEqual(frameId, unchangedFrame.GetProperty("id").GetInt32());
            await FinishResultsViewSessionAsync(client).ConfigureAwait(false);
        }
        finally
        {
            File.Delete(waitPath);
        }
    }

    private Task<DapTestClient> StartNullableAssignmentFixtureAsync(string waitPath, string? assemblyPath = null) =>
        StartPresentationFixtureAsync(waitPath, "NullableAssignmentDebuggerFixture.cs",
            "int result = DebuggerFixture.WaitForSignal(", "--debugger-nullable-assignment-fixture", assemblyPath);

    private async Task AssertNullableGenericFieldNamesAsync(DapTestClient client, int frameId)
    {
        JsonElement pair = await ReadEvaluationAsync(client, frameId, "pair", success: true,
            TestContext.CancellationToken).ConfigureAwait(false);
        JsonElement[] rows = await ReadVariablesAsync(client, pair.GetProperty("variablesReference").GetInt32())
            .ConfigureAwait(false);
        JsonElement raw = Assert.ContainsSingle(rows.Where(row => row.GetProperty("name").GetString() == "Raw View"));
        JsonElement[] fields = await ReadVariablesAsync(client, raw.GetProperty("variablesReference").GetInt32())
            .ConfigureAwait(false);
        JsonElement key = Assert.ContainsSingle(fields.Where(field => field.GetProperty("name").GetString() == "key"));
        JsonElement value = Assert.ContainsSingle(fields.Where(field => field.GetProperty("name").GetString() == "value"));
        await AssertStructAssignmentTupleChildrenAsync(client, key, "pair.key", "KeyNumber", "KeyText",
            "216", "\"key\"").ConfigureAwait(false);
        await AssertNullableTupleStorageAsync(client, value, "pair.value", "ValueNumber", "ValueText", hasValue: true,
            "217", "\"value\"").ConfigureAwait(false);
        JsonElement evaluated = await ReadEvaluationAsync(client, frameId, "pair.value", success: true,
            TestContext.CancellationToken).ConfigureAwait(false);
        await AssertNullableTupleStorageAsync(client, evaluated, "pair.value", "ValueNumber", "ValueText", hasValue: true,
            "217", "\"value\"").ConfigureAwait(false);
    }

    private async Task AssertNullableTupleStorageAsync(
        DapTestClient client,
        JsonElement nullable,
        string expression,
        string numberName,
        string textName,
        bool hasValue,
        string number,
        string text)
    {
        Assert.AreEqual($"(int {numberName}, string {textName})?", nullable.GetProperty("type").GetString());
        int reference = nullable.GetProperty("variablesReference").GetInt32();
        Assert.IsGreaterThan(0, reference);
        JsonElement[] children = await ReadVariablesAsync(client, reference).ConfigureAwait(false);
        JsonElement presence = Assert.ContainsSingle(children.Where(child =>
            child.GetProperty("name").GetString() == "hasValue"));
        Assert.AreEqual(hasValue ? "true" : "false", presence.GetProperty("value").GetString());
        Assert.AreEqual("bool", presence.GetProperty("type").GetString());
        Assert.AreEqual(0, presence.GetProperty("variablesReference").GetInt32());
        JsonElement payload = Assert.ContainsSingle(children.Where(child =>
            child.GetProperty("name").GetString() == "value"));
        await AssertStructAssignmentTupleChildrenAsync(client, payload, expression + ".value", numberName,
            textName, number, text).ConfigureAwait(false);
    }
}
