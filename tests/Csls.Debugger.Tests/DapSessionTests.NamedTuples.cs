using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Csls.Debugger.Tests;

/// <summary>
/// Verifies source-authored tuple names over real stopped runtime values.
/// </summary>
public sealed partial class DapSessionTests
{
    /// <summary>
    /// Preserves tuple names through inspection, evaluation, completion, and assignment.
    /// </summary>
    [TestMethod]
    [Timeout(30000, CooperativeCancellation = true)]
    public async Task NamedTuplesPreserveSourceNamesAcrossDebuggerOperations()
    {
        string waitPath = Path.Join(
            Path.GetTempPath(),
            $"csls-debugger-named-tuples-{Guid.NewGuid():N}.signal");
        try
        {
            DapTestClient client = await StartStoppedFixtureAsync(waitPath).ConfigureAwait(false);
            await using ConfiguredAsyncDisposable disposal = client.ConfigureAwait(false);
            JsonElement frame = await GetFixtureFrameAsync(client).ConfigureAwait(false);
            int frameId = frame.GetProperty("id").GetInt32();
            (int argumentsReference, int localsReference) = await ReadFrameScopeReferencesAsync(
                client,
                frameId).ConfigureAwait(false);
            Dictionary<string, JsonElement> arguments = (await ReadVariablesAsync(
                client,
                argumentsReference).ConfigureAwait(false)).ToDictionary(
                    variable => variable.GetProperty("name").GetString()!,
                    StringComparer.Ordinal);
            Dictionary<string, JsonElement> locals = (await ReadVariablesAsync(
                client,
                localsReference).ConfigureAwait(false)).ToDictionary(
                    variable => variable.GetProperty("name").GetString()!,
                    StringComparer.Ordinal);

            await AssertNamedTupleArgumentAsync(client, arguments["tupleArgument"])
                .ConfigureAwait(false);
            int tupleReference = await AssertNamedTupleLocalAsync(client, locals["localTuple"])
                .ConfigureAwait(false);
            await AssertNestedTupleAsync(client, frameId, locals["localNestedTuple"])
                .ConfigureAwait(false);
            await AssertLongNamedTupleAsync(client, locals["localLongTuple"])
                .ConfigureAwait(false);
            await AssertEightElementNamedTupleAsync(client, locals["localEightTuple"])
                .ConfigureAwait(false);
            await AssertSixteenElementNamedTupleAsync(client, locals["localSixteenTuple"])
                .ConfigureAwait(false);
            await AssertNamedTupleArrayAsync(client, locals["localTupleArray"])
                .ConfigureAwait(false);
            await AssertNamedTupleFieldAsync(client, locals["localObject"])
                .ConfigureAwait(false);

            JsonElement[] completions = await ReadCompletionsAsync(
                client,
                frameId,
                "localTuple.N",
                TestContext.CancellationToken).ConfigureAwait(false);
            Assert.Contains(
                "Number",
                completions.Select(completion => completion.GetProperty("label").GetString()!));
            Assert.AreEqual(
                "42",
                (await ReadEvaluationAsync(
                    client,
                    frameId,
                    "localTuple.Number",
                    success: true,
                    TestContext.CancellationToken).ConfigureAwait(false))
                    .GetProperty("result")
                    .GetString());

            JsonElement setVariable = await ReadSetVariableAsync(
                client,
                tupleReference,
                "Number",
                "50",
                success: true,
                TestContext.CancellationToken).ConfigureAwait(false);
            Assert.AreEqual("50", setVariable.GetProperty("value").GetString());
            Assert.AreEqual(
                "50",
                (await ReadEvaluationAsync(
                    client,
                    frameId,
                    "localTuple.Number",
                    success: true,
                    TestContext.CancellationToken).ConfigureAwait(false))
                    .GetProperty("result")
                    .GetString());
            Assert.AreEqual(
                "\"answer\"",
                (await ReadEvaluationAsync(
                    client,
                    frameId,
                    "localTuple.Text",
                    success: true,
                    TestContext.CancellationToken).ConfigureAwait(false))
                    .GetProperty("result")
                    .GetString());
            JsonElement setExpression = await ReadSetExpressionAsync(
                client,
                frameId,
                "localTuple.Number",
                "51",
                success: true,
                TestContext.CancellationToken).ConfigureAwait(false);
            Assert.AreEqual("51", setExpression.GetProperty("value").GetString());
            Assert.AreEqual(
                "51",
                (await ReadEvaluationAsync(
                    client,
                    frameId,
                    "localTuple.Number",
                    success: true,
                    TestContext.CancellationToken).ConfigureAwait(false))
                    .GetProperty("result")
                    .GetString());

            await ResumeAndReleaseFixtureAsync(client, waitPath).ConfigureAwait(false);
            Assert.AreEqual(string.Empty, client.Diagnostics.ToString());
        }
        finally
        {
            File.Delete(waitPath);
        }
    }

    private async Task<(int Arguments, int Locals)> ReadFrameScopeReferencesAsync(
        DapTestClient client,
        int frameId)
    {
        int sequence = await client.SendRequestAsync(
            "scopes",
            writer => WriteFrameArguments(writer, frameId),
            TestContext.CancellationToken).ConfigureAwait(false);
        using JsonDocument response = await client
            .ReadMessageAsync(TestContext.CancellationToken)
            .ConfigureAwait(false);
        AssertResponse(response.RootElement, sequence, "scopes", success: true);
        var scopes = response.RootElement
            .GetProperty("body")
            .GetProperty("scopes")
            .EnumerateArray()
            .ToDictionary(
                scope => scope.GetProperty("name").GetString()!,
                scope => scope.GetProperty("variablesReference").GetInt32(),
                StringComparer.Ordinal);
        return (scopes["Arguments"], scopes["Locals"]);
    }

    private async Task AssertNamedTupleArgumentAsync(
        DapTestClient client,
        JsonElement argument)
    {
        Assert.AreEqual(
            "(int ArgumentNumber, string ArgumentText)",
            argument.GetProperty("type").GetString());
        JsonElement[] children = await ReadVariablesAsync(
            client,
            argument.GetProperty("variablesReference").GetInt32()).ConfigureAwait(false);
        Assert.AreSequenceEqual(
            ["ArgumentNumber", "ArgumentText", "Raw View"],
            children.Select(child => child.GetProperty("name").GetString()).ToArray());
        Assert.AreEqual(
            "tupleArgument.Item1",
            children[0].GetProperty("evaluateName").GetString());
    }

    private async Task<int> AssertNamedTupleLocalAsync(
        DapTestClient client,
        JsonElement local)
    {
        Assert.AreEqual("(int Number, string Text)", local.GetProperty("type").GetString());
        int reference = local.GetProperty("variablesReference").GetInt32();
        JsonElement[] children = await ReadVariablesAsync(client, reference).ConfigureAwait(false);
        Assert.AreSequenceEqual(
            ["Number", "Text", "Raw View"],
            children.Select(child => child.GetProperty("name").GetString()).ToArray());
        Assert.AreEqual("localTuple.Item1", children[0].GetProperty("evaluateName").GetString());
        Assert.AreEqual("localTuple.Item2", children[1].GetProperty("evaluateName").GetString());
        int rawReference = children[2].GetProperty("variablesReference").GetInt32();
        Assert.AreSequenceEqual(
            ["Item1", "Item2"],
            (await ReadVariablesAsync(client, rawReference).ConfigureAwait(false))
                .Select(child => child.GetProperty("name").GetString())
                .ToArray());
        return reference;
    }

    private async Task AssertNestedTupleAsync(
        DapTestClient client,
        int frameId,
        JsonElement local)
    {
        Assert.AreEqual(
            "((int InnerNumber, string InnerText) Inner, int OuterNumber)",
            local.GetProperty("type").GetString());
        JsonElement[] outer = await ReadVariablesAsync(
            client,
            local.GetProperty("variablesReference").GetInt32()).ConfigureAwait(false);
        Assert.AreSequenceEqual(
            ["Inner", "OuterNumber", "Raw View"],
            outer.Select(child => child.GetProperty("name").GetString()).ToArray());
        JsonElement[] inner = await ReadVariablesAsync(
            client,
            outer[0].GetProperty("variablesReference").GetInt32()).ConfigureAwait(false);
        Assert.AreSequenceEqual(
            ["InnerNumber", "InnerText", "Raw View"],
            inner.Select(child => child.GetProperty("name").GetString()).ToArray());
        Assert.AreEqual(
            "localNestedTuple.Item1.Item1",
            inner[0].GetProperty("evaluateName").GetString());
        Assert.AreEqual(
            "42",
            (await ReadEvaluationAsync(
                client,
                frameId,
                "localNestedTuple.Inner.InnerNumber",
                success: true,
                TestContext.CancellationToken).ConfigureAwait(false))
                .GetProperty("result")
                .GetString());
    }

    private async Task AssertLongNamedTupleAsync(DapTestClient client, JsonElement local)
    {
        Assert.AreEqual(
            "(int One, int Two, int Three, int Four, int Five, int Six, int Seven, int Eight, int Nine)",
            local.GetProperty("type").GetString());
        JsonElement[] page = await ReadVariablesAsync(
            client,
            local.GetProperty("variablesReference").GetInt32(),
            start: 7,
            count: 2).ConfigureAwait(false);
        Assert.AreSequenceEqual(
            ["Eight", "Nine"],
            page.Select(child => child.GetProperty("name").GetString()).ToArray());
        Assert.AreEqual(
            "localLongTuple.Rest.Item1",
            page[0].GetProperty("evaluateName").GetString());
        Assert.AreEqual(
            "localLongTuple.Rest.Item2",
            page[1].GetProperty("evaluateName").GetString());
    }

    private async Task AssertNamedTupleArrayAsync(DapTestClient client, JsonElement local)
    {
        Assert.AreEqual("(int Number, string Text)[]", local.GetProperty("type").GetString());
        JsonElement element = Assert.ContainsSingle(await ReadVariablesAsync(
            client,
            local.GetProperty("variablesReference").GetInt32()).ConfigureAwait(false));
        Assert.AreEqual("(int Number, string Text)", element.GetProperty("type").GetString());
        JsonElement[] fields = await ReadVariablesAsync(
            client,
            element.GetProperty("variablesReference").GetInt32()).ConfigureAwait(false);
        Assert.AreSequenceEqual(
            ["Number", "Text", "Raw View"],
            fields.Select(field => field.GetProperty("name").GetString()).ToArray());
    }

    private async Task AssertEightElementNamedTupleAsync(
        DapTestClient client,
        JsonElement local)
    {
        Assert.AreEqual(
            "(int One, int Two, int Three, int Four, int Five, int Six, int Seven, " +
            "int Eight)",
            local.GetProperty("type").GetString());
        JsonElement element = Assert.ContainsSingle(await ReadVariablesAsync(
            client,
            local.GetProperty("variablesReference").GetInt32(),
            start: 7,
            count: 1).ConfigureAwait(false));
        Assert.AreEqual("Eight", element.GetProperty("name").GetString());
        Assert.AreEqual(
            "localEightTuple.Rest.Item1",
            element.GetProperty("evaluateName").GetString());
    }

    private async Task AssertSixteenElementNamedTupleAsync(
        DapTestClient client,
        JsonElement local)
    {
        Assert.AreEqual(
            "(int One, int Two, int Three, int Four, int Five, int Six, int Seven, " +
            "int Eight, int Nine, int Ten, int Eleven, int Twelve, int Thirteen, " +
            "int Fourteen, int Fifteen, int Sixteen)",
            local.GetProperty("type").GetString());
        JsonElement[] page = await ReadVariablesAsync(
            client,
            local.GetProperty("variablesReference").GetInt32(),
            start: 14,
            count: 2).ConfigureAwait(false);
        Assert.AreSequenceEqual(
            ["Fifteen", "Sixteen"],
            page.Select(element => element.GetProperty("name").GetString()).ToArray());
        Assert.AreEqual(
            "localSixteenTuple.Rest.Rest.Item1",
            page[0].GetProperty("evaluateName").GetString());
        Assert.AreEqual(
            "localSixteenTuple.Rest.Rest.Item2",
            page[1].GetProperty("evaluateName").GetString());
        Assert.AreEqual("15", page[0].GetProperty("value").GetString());
        Assert.AreEqual("16", page[1].GetProperty("value").GetString());
    }

    private async Task AssertNamedTupleFieldAsync(DapTestClient client, JsonElement localObject)
    {
        JsonElement pair = (await ReadVariablesAsync(
            client,
            localObject.GetProperty("variablesReference").GetInt32()).ConfigureAwait(false))
            .Single(field => field.GetProperty("name").GetString() == "Pair");
        Assert.AreEqual("(int Code, string Label)", pair.GetProperty("type").GetString());
        JsonElement[] fields = await ReadVariablesAsync(
            client,
            pair.GetProperty("variablesReference").GetInt32()).ConfigureAwait(false);
        Assert.AreSequenceEqual(
            ["Code", "Label", "Raw View"],
            fields.Select(field => field.GetProperty("name").GetString()).ToArray());
    }
}
