using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Csls.Debugger.Tests;

/// <summary>
/// Verifies lazy enumerable presentation through real DAP requests and target execution.
/// </summary>
public sealed partial class DapSessionTests
{
    /// <summary>
    /// Selects the most specific enumerable interface and executes it only on expansion.
    /// </summary>
    /// <param name="localName">The target enumerable retained in the stopped frame.</param>
    /// <param name="expectedValues">The ordered values expected from its selected interface.</param>
    [TestMethod]
    [DataRow("localResultsView", new[] { "71", "72", "73" })]
    [DataRow("localResultsViewNonGeneric", new[] { "81", "82" })]
    [DataRow("localResultsViewMultiple", new[] { "91", "92" })]
    [Timeout(30000, CooperativeCancellation = true)]
    public async Task ResultsViewSelectsEnumerableWithoutEagerExecution(
        string localName,
        string[] expectedValues)
    {
        ArgumentNullException.ThrowIfNull(expectedValues);
        string waitPath = CreateResultsViewSignalPath();
        try
        {
            DapTestClient client = await StartProxyFixtureAsync(waitPath).ConfigureAwait(false);
            await using ConfiguredAsyncDisposable disposal = client.ConfigureAwait(false);
            JsonElement row = await ReadResultsViewRowAsync(client, localName)
                .ConfigureAwait(false);
            await AssertEnumerationCountAsync(client, localName, 0).ConfigureAwait(false);

            int retiredReference = row.GetProperty("variablesReference").GetInt32();
            JsonElement[] items = await ExpandResultsViewAsync(client, retiredReference)
                .ConfigureAwait(false);
            Assert.AreSequenceEqual(
                expectedValues,
                items.Select(item => item.GetProperty("value").GetString()).ToArray());
            Assert.AreSequenceEqual(
                Enumerable.Range(0, expectedValues.Length).Select(index => $"[{index}]").ToArray(),
                items.Select(item => item.GetProperty("name").GetString()).ToArray());
            foreach (JsonElement item in items)
            {
                Assert.AreEqual("int", item.GetProperty("type").GetString());
                Assert.IsFalse(item.TryGetProperty("evaluateName", out _));
            }

            await AssertEnumerationCountAsync(client, localName, 1).ConfigureAwait(false);
            int staleSequence = await client.SendRequestAsync(
                "variables",
                writer => WriteResultsViewReference(writer, retiredReference),
                TestContext.CancellationToken).ConfigureAwait(false);
            using JsonDocument staleResponse = await client
                .ReadMessageAsync(TestContext.CancellationToken).ConfigureAwait(false);
            AssertResponse(staleResponse.RootElement, staleSequence, "variables", success: false);

            await FinishResultsViewSessionAsync(client).ConfigureAwait(false);
        }
        finally
        {
            File.Delete(waitPath);
        }
    }

    /// <summary>
    /// Enumerates value types and preserves shared execution state through nullable and boxed forms.
    /// </summary>
    /// <param name="localName">The unboxed, nullable, or boxed enumerable local.</param>
    [TestMethod]
    [DataRow("localResultsViewStruct")]
    [DataRow("localResultsViewNullableStruct")]
    [DataRow("localResultsViewBoxedStruct")]
    [Timeout(30000, CooperativeCancellation = true)]
    public async Task ResultsViewPreservesStructNullableAndBoxedEnumerables(string localName)
    {
        string waitPath = CreateResultsViewSignalPath();
        try
        {
            DapTestClient client = await StartProxyFixtureAsync(waitPath).ConfigureAwait(false);
            await using ConfiguredAsyncDisposable disposal = client.ConfigureAwait(false);
            JsonElement row = await ReadResultsViewRowAsync(client, localName).ConfigureAwait(false);
            await AssertStructEnumerationCountAsync(client, localName, 0).ConfigureAwait(false);
            JsonElement[] items = await ExpandResultsViewAsync(
                client,
                row.GetProperty("variablesReference").GetInt32()).ConfigureAwait(false);
            Assert.AreSequenceEqual(
                ["151", "152"],
                items.Select(item => item.GetProperty("value").GetString()).ToArray());
            Assert.AreSequenceEqual(
                ["[0]", "[1]"],
                items.Select(item => item.GetProperty("name").GetString()).ToArray());
            foreach (JsonElement item in items)
            {
                Assert.AreEqual("int", item.GetProperty("type").GetString());
                Assert.IsFalse(item.TryGetProperty("evaluateName", out _));
            }

            await AssertStructEnumerationCountAsync(client, localName, 1).ConfigureAwait(false);
            await FinishResultsViewSessionAsync(client).ConfigureAwait(false);
        }
        finally
        {
            File.Delete(waitPath);
        }
    }

    /// <summary>
    /// Preserves substituted array element types through inherited enumerable interfaces.
    /// </summary>
    [TestMethod]
    [Timeout(30000, CooperativeCancellation = true)]
    public async Task ResultsViewResolvesInheritedGenericArrayElements()
    {
        string waitPath = CreateResultsViewSignalPath();
        try
        {
            DapTestClient client = await StartProxyFixtureAsync(waitPath).ConfigureAwait(false);
            await using ConfiguredAsyncDisposable disposal = client.ConfigureAwait(false);
            JsonElement row = await ReadResultsViewRowAsync(client, "localResultsViewInherited")
                .ConfigureAwait(false);
            JsonElement[] items = await ExpandResultsViewAsync(
                client,
                row.GetProperty("variablesReference").GetInt32()).ConfigureAwait(false);
            Assert.AreSequenceEqual(
                ["[0]", "[1]"],
                items.Select(item => item.GetProperty("name").GetString()).ToArray());
            Assert.AreSequenceEqual(
                ["int[]", "int[]"],
                items.Select(item => item.GetProperty("type").GetString()).ToArray());
            for (int index = 0; index < items.Length; index++)
            {
                Assert.IsFalse(items[index].TryGetProperty("evaluateName", out _));
                JsonElement element = Assert.ContainsSingle(await ReadVariablesAsync(
                    client,
                    items[index].GetProperty("variablesReference").GetInt32())
                    .ConfigureAwait(false));
                Assert.AreEqual((101 + index).ToString(System.Globalization.CultureInfo.InvariantCulture),
                    element.GetProperty("value").GetString());
                Assert.IsFalse(element.TryGetProperty("evaluateName", out _));
            }

            await AssertEnumerationCountAsync(client, "localResultsViewInherited", 1)
                .ConfigureAwait(false);
            await FinishResultsViewSessionAsync(client).ConfigureAwait(false);
        }
        finally
        {
            File.Delete(waitPath);
        }
    }

    /// <summary>
    /// Preserves rectangular array shapes inside the selected generic enumerable type.
    /// </summary>
    [TestMethod]
    [Timeout(30000, CooperativeCancellation = true)]
    public async Task ResultsViewPreservesRectangularArrayElementShape()
    {
        string waitPath = CreateResultsViewSignalPath();
        try
        {
            DapTestClient client = await StartProxyFixtureAsync(waitPath).ConfigureAwait(false);
            await using ConfiguredAsyncDisposable disposal = client.ConfigureAwait(false);
            JsonElement row = await ReadResultsViewRowAsync(client, "localResultsViewRectangular")
                .ConfigureAwait(false);
            JsonElement array = Assert.ContainsSingle(await ExpandResultsViewAsync(
                client,
                row.GetProperty("variablesReference").GetInt32()).ConfigureAwait(false));
            Assert.AreEqual("int[,]", array.GetProperty("type").GetString());
            Assert.IsFalse(array.TryGetProperty("evaluateName", out _));
            JsonElement[] elements = await ReadVariablesAsync(
                client,
                array.GetProperty("variablesReference").GetInt32()).ConfigureAwait(false);
            Assert.AreSequenceEqual(
                ["[0,0]", "[0,1]"],
                elements.Select(element => element.GetProperty("name").GetString()).ToArray());
            Assert.AreSequenceEqual(
                ["103", "104"],
                elements.Select(element => element.GetProperty("value").GetString()).ToArray());
            foreach (JsonElement element in elements)
            {
                Assert.IsFalse(element.TryGetProperty("evaluateName", out _));
            }

            await AssertEnumerationCountAsync(client, "localResultsViewRectangular", 1)
                .ConfigureAwait(false);
            await FinishResultsViewSessionAsync(client).ConfigureAwait(false);
        }
        finally
        {
            File.Delete(waitPath);
        }
    }

    /// <summary>
    /// Preserves the runtime identity of element types from separate assembly load contexts.
    /// </summary>
    /// <param name="localName">The enumerable from the default or isolated assembly instance.</param>
    [TestMethod]
    [DataRow("localResultsViewDefaultContext")]
    [DataRow("localResultsViewIsolatedContext")]
    [Timeout(30000, CooperativeCancellation = true)]
    public async Task ResultsViewPreservesElementAssemblyLoadContext(string localName)
    {
        string waitPath = CreateResultsViewSignalPath();
        try
        {
            DapTestClient client = await StartProxyFixtureAsync(waitPath, isolateResultsViewAssembly: true)
                .ConfigureAwait(false);
            await using ConfiguredAsyncDisposable disposal = client.ConfigureAwait(false);
            JsonElement row = await ReadResultsViewRowAsync(client, localName).ConfigureAwait(false);
            JsonElement item = Assert.ContainsSingle(await ExpandResultsViewAsync(
                client,
                row.GetProperty("variablesReference").GetInt32()).ConfigureAwait(false));
            Assert.AreEqual("Csls.TestProcessHost.ResultsViewElement", item.GetProperty("type").GetString());
            Assert.IsFalse(item.TryGetProperty("evaluateName", out _));
            JsonElement field = Assert.ContainsSingle(await ReadVariablesAsync(
                client,
                item.GetProperty("variablesReference").GetInt32()).ConfigureAwait(false));
            Assert.AreEqual("_value", field.GetProperty("name").GetString());
            Assert.AreEqual("131", field.GetProperty("value").GetString());
            await AssertEnumerationCountAsync(client, localName, 1).ConfigureAwait(false);
            await FinishResultsViewSessionAsync(client).ConfigureAwait(false);
        }
        finally
        {
            File.Delete(waitPath);
        }
    }

    /// <summary>
    /// Pages the lazy row and materialized items using their logical collection positions.
    /// </summary>
    [TestMethod]
    [Timeout(30000, CooperativeCancellation = true)]
    public async Task ResultsViewPagesDiscoveryAndEnumeratedItems()
    {
        string waitPath = CreateResultsViewSignalPath();
        try
        {
            DapTestClient client = await StartProxyFixtureAsync(waitPath).ConfigureAwait(false);
            await using ConfiguredAsyncDisposable disposal = client.ConfigureAwait(false);
            JsonElement local = await ReadResultsViewLocalAsync(client, "localResultsView")
                .ConfigureAwait(false);
            int parentReference = local.GetProperty("variablesReference").GetInt32();
            JsonElement[] fields = await ReadVariablesAsync(client, parentReference)
                .ConfigureAwait(false);
            JsonElement finalRow = Assert.ContainsSingle(await ReadVariablesAsync(
                client,
                parentReference,
                start: fields.Length - 1,
                count: 1).ConfigureAwait(false));
            Assert.AreEqual("Results View", finalRow.GetProperty("name").GetString());
            Assert.IsEmpty(await ReadVariablesAsync(
                client,
                parentReference,
                start: fields.Length,
                count: 1).ConfigureAwait(false));
            await AssertEnumerationCountAsync(client, "localResultsView", 0).ConfigureAwait(false);

            (int Start, int Count, string[] Names, string[] Values)[] pages =
            [
                (0, 1, ["[0]"], ["71"]),
                (1, 1, ["[1]"], ["72"]),
                (2, 4, ["[2]"], ["73"]),
                (3, 1, [], [])
            ];
            for (int index = 0; index < pages.Length; index++)
            {
                JsonElement row = await ReadResultsViewRowAsync(client, "localResultsView")
                    .ConfigureAwait(false);
                JsonElement[] items = await ExpandResultsViewAsync(
                    client,
                    row.GetProperty("variablesReference").GetInt32(),
                    pages[index].Start,
                    pages[index].Count).ConfigureAwait(false);
                Assert.AreSequenceEqual(
                    pages[index].Names,
                    items.Select(item => item.GetProperty("name").GetString()).ToArray());
                Assert.AreSequenceEqual(
                    pages[index].Values,
                    items.Select(item => item.GetProperty("value").GetString()).ToArray());
                await AssertEnumerationCountAsync(client, "localResultsView", index + 1)
                    .ConfigureAwait(false);
            }

            await FinishResultsViewSessionAsync(client).ConfigureAwait(false);
        }
        finally
        {
            File.Delete(waitPath);
        }
    }

    /// <summary>
    /// Presents the runtime empty-enumerable sentinel as a read-only string row.
    /// </summary>
    [TestMethod]
    [Timeout(30000, CooperativeCancellation = true)]
    public async Task ResultsViewPresentsEmptyEnumeration()
    {
        string waitPath = CreateResultsViewSignalPath();
        try
        {
            DapTestClient client = await StartProxyFixtureAsync(waitPath).ConfigureAwait(false);
            await using ConfiguredAsyncDisposable disposal = client.ConfigureAwait(false);
            JsonElement row = await ReadResultsViewRowAsync(client, "localResultsViewEmpty")
                .ConfigureAwait(false);
            await AssertEnumerationCountAsync(client, "localResultsViewEmpty", 0)
                .ConfigureAwait(false);
            JsonElement empty = Assert.ContainsSingle(await ExpandResultsViewAsync(
                client,
                row.GetProperty("variablesReference").GetInt32()).ConfigureAwait(false));
            Assert.AreEqual("Empty", empty.GetProperty("name").GetString());
            Assert.AreEqual("\"Enumeration yielded no results\"", empty.GetProperty("value").GetString());
            Assert.AreEqual("string", empty.GetProperty("type").GetString());
            Assert.AreEqual(0, empty.GetProperty("variablesReference").GetInt32());
            Assert.IsFalse(empty.TryGetProperty("evaluateName", out _));
            Assert.AreSequenceEqual(
                ["readOnly", "rawString"],
                empty.GetProperty("presentationHint").GetProperty("attributes")
                    .EnumerateArray().Select(attribute => attribute.GetString()).ToArray());
            await AssertEnumerationCountAsync(client, "localResultsViewEmpty", 1)
                .ConfigureAwait(false);
            JsonElement nextRow = await ReadResultsViewRowAsync(client, "localResultsViewEmpty")
                .ConfigureAwait(false);
            Assert.IsEmpty(await ExpandResultsViewAsync(
                client,
                nextRow.GetProperty("variablesReference").GetInt32(),
                start: 1,
                count: 1).ConfigureAwait(false));
            await AssertEnumerationCountAsync(client, "localResultsViewEmpty", 2)
                .ConfigureAwait(false);
            await FinishResultsViewSessionAsync(client).ConfigureAwait(false);
        }
        finally
        {
            File.Delete(waitPath);
        }
    }

    /// <summary>
    /// Reports enumeration exceptions while preserving stopped-frame inspection.
    /// </summary>
    [TestMethod]
    [Timeout(30000, CooperativeCancellation = true)]
    public async Task ResultsViewEnumerationFailureKeepsSessionUsable()
    {
        string waitPath = CreateResultsViewSignalPath();
        try
        {
            DapTestClient client = await StartProxyFixtureAsync(waitPath).ConfigureAwait(false);
            await using ConfiguredAsyncDisposable disposal = client.ConfigureAwait(false);
            JsonElement row = await ReadResultsViewRowAsync(client, "localResultsViewThrowing")
                .ConfigureAwait(false);
            int sequence = await client.SendRequestAsync(
                "variables",
                writer => WriteResultsViewReference(writer, row.GetProperty("variablesReference").GetInt32()),
                TestContext.CancellationToken).ConfigureAwait(false);
            using JsonDocument response = await client
                .ReadMessageAsync(TestContext.CancellationToken).ConfigureAwait(false);
            AssertResponse(response.RootElement, sequence, "variables", success: false);
            string? message = response.RootElement.GetProperty("message").GetString();
            Assert.IsNotNull(message);
            Assert.Contains("InvalidOperationException", message);
            Assert.Contains("Results View fixture enumeration failed.", message);
            await ReadResultsViewInvalidationAsync(client).ConfigureAwait(false);
            await AssertEnumerationCountAsync(client, "localResultsViewThrowing", 1)
                .ConfigureAwait(false);
            JsonElement number = await ReadResultsViewLocalAsync(client, "localNumber")
                .ConfigureAwait(false);
            Assert.AreEqual("43", number.GetProperty("value").GetString());
            await FinishResultsViewSessionAsync(client).ConfigureAwait(false);
        }
        finally
        {
            File.Delete(waitPath);
        }
    }

    /// <summary>
    /// Cancels a blocked enumerator, runs iterator cleanup, and permits another evaluation.
    /// </summary>
    [TestMethod]
    [Timeout(30000, CooperativeCancellation = true)]
    public async Task ResultsViewCancellationDisposesIteratorAndRestoresEvaluation()
    {
        string waitPath = CreateResultsViewSignalPath();
        string signalPath = waitPath + ".results-view-started";
        try
        {
            DapTestClient client = await StartProxyFixtureAsync(waitPath).ConfigureAwait(false);
            await using ConfiguredAsyncDisposable disposal = client.ConfigureAwait(false);
            JsonElement row = await ReadResultsViewRowAsync(client, "localResultsViewBlocking")
                .ConfigureAwait(false);
            int sequence = await client.SendRequestAsync(
                "variables",
                writer => WriteResultsViewReference(writer, row.GetProperty("variablesReference").GetInt32()),
                TestContext.CancellationToken).ConfigureAwait(false);
            await WaitForSignalAsync(signalPath).ConfigureAwait(false);
            int cancelSequence = await client.SendRequestAsync(
                "cancel",
                writer =>
                {
                    writer.WriteStartObject();
                    writer.WriteNumber("requestId", sequence);
                    writer.WriteEndObject();
                },
                TestContext.CancellationToken).ConfigureAwait(false);
            await AssertCanceledTargetCodeOperationAsync(client, sequence, cancelSequence, "variables")
                .ConfigureAwait(false);
            JsonElement[] fields = await ReadUnproxiedLocalAsync(client, "localResultsViewBlocking")
                .ConfigureAwait(false);
            Assert.AreEqual("1", fields.Single(field =>
                field.GetProperty("name").GetString() == "_enumerationCount")
                .GetProperty("value").GetString());
            Assert.AreEqual("1", fields.Single(field =>
                field.GetProperty("name").GetString() == "_disposeCount")
                .GetProperty("value").GetString());
            JsonElement nextRow = await ReadResultsViewRowAsync(client, "localResultsView")
                .ConfigureAwait(false);
            JsonElement[] items = await ExpandResultsViewAsync(
                client,
                nextRow.GetProperty("variablesReference").GetInt32()).ConfigureAwait(false);
            Assert.AreSequenceEqual(
                ["71", "72", "73"],
                items.Select(item => item.GetProperty("value").GetString()).ToArray());
            await FinishResultsViewSessionAsync(client).ConfigureAwait(false);
        }
        finally
        {
            File.Delete(waitPath);
            File.Delete(signalPath);
            File.Delete(signalPath + ".release");
        }
    }

    /// <summary>
    /// Excludes strings, arrays, nulls, pattern-only objects, and successful debugger proxies.
    /// </summary>
    [TestMethod]
    [Timeout(30000, CooperativeCancellation = true)]
    public async Task ResultsViewHonorsExcludedValuesAndDebuggerProxyPresentation()
    {
        string waitPath = CreateResultsViewSignalPath();
        try
        {
            DapTestClient client = await StartProxyFixtureAsync(waitPath).ConfigureAwait(false);
            await using ConfiguredAsyncDisposable disposal = client.ConfigureAwait(false);
            foreach (string localName in new[] { "localText", "localResultsViewNull" })
            {
                JsonElement local = await ReadResultsViewLocalAsync(client, localName)
                    .ConfigureAwait(false);
                Assert.AreEqual(0, local.GetProperty("variablesReference").GetInt32(), localName);
            }

            foreach (string localName in new[] { "localArray", "localResultsViewPattern" })
            {
                JsonElement[] fields = await ReadUnproxiedLocalAsync(client, localName)
                    .ConfigureAwait(false);
                Assert.IsNotEmpty(fields, localName);
                Assert.DoesNotContain("Results View",
                    fields.Select(field => field.GetProperty("name").GetString()), localName);
            }

            JsonElement[] proxy = await ReadProxyLocalAsync(client, "localResultsViewProxied")
                .ConfigureAwait(false);
            Assert.AreSequenceEqual(
                ["Value", "Raw View"],
                proxy.Select(field => field.GetProperty("name").GetString()).ToArray());
            Assert.AreEqual("112", proxy[0].GetProperty("value").GetString());
            JsonElement[] raw = await ReadVariablesAsync(
                client,
                proxy[1].GetProperty("variablesReference").GetInt32()).ConfigureAwait(false);
            Assert.DoesNotContain("Results View",
                raw.Select(field => field.GetProperty("name").GetString()));
            Assert.AreEqual("0", raw.Single(field =>
                field.GetProperty("name").GetString() == "_enumerationCount")
                .GetProperty("value").GetString());
            await FinishResultsViewSessionAsync(client).ConfigureAwait(false);
        }
        finally
        {
            File.Delete(waitPath);
        }
    }

    private static string CreateResultsViewSignalPath() => Path.Join(
        Path.GetTempPath(), $"csls-debugger-results-view-{Guid.NewGuid():N}.signal");

    private static void WriteResultsViewReference(Utf8JsonWriter writer, int reference)
    {
        writer.WriteStartObject();
        writer.WriteNumber("variablesReference", reference);
        writer.WriteEndObject();
    }

    private async Task<JsonElement> ReadResultsViewLocalAsync(DapTestClient client, string localName)
    {
        JsonElement frame = await GetFixtureFrameAsync(client).ConfigureAwait(false);
        int sequence = await client.SendRequestAsync(
            "scopes",
            writer => WriteFrameArguments(writer, frame.GetProperty("id").GetInt32()),
            TestContext.CancellationToken).ConfigureAwait(false);
        using JsonDocument scopes = await client.ReadMessageAsync(TestContext.CancellationToken)
            .ConfigureAwait(false);
        AssertResponse(scopes.RootElement, sequence, "scopes", success: true);
        JsonElement locals = scopes.RootElement.GetProperty("body").GetProperty("scopes")
            .EnumerateArray().Single(scope => scope.GetProperty("name").GetString() == "Locals");
        return (await ReadVariablesAsync(client, locals.GetProperty("variablesReference").GetInt32())
            .ConfigureAwait(false)).Single(local => local.GetProperty("name").GetString() == localName);
    }

    private async Task<JsonElement> ReadResultsViewRowAsync(DapTestClient client, string localName)
    {
        JsonElement[] fields = await ReadUnproxiedLocalAsync(client, localName).ConfigureAwait(false);
        JsonElement row = Assert.ContainsSingle(fields.Where(field =>
            field.GetProperty("name").GetString() == "Results View"));
        Assert.AreEqual("Expanding the Results View will enumerate the IEnumerable",
            row.GetProperty("value").GetString());
        Assert.IsGreaterThan(0, row.GetProperty("variablesReference").GetInt32());
        Assert.IsFalse(row.TryGetProperty("evaluateName", out _));
        JsonElement hint = row.GetProperty("presentationHint");
        Assert.AreEqual("virtual", hint.GetProperty("kind").GetString());
        Assert.IsTrue(hint.GetProperty("lazy").GetBoolean());
        Assert.AreSequenceEqual(
            ["readOnly", "hasSideEffects"],
            hint.GetProperty("attributes").EnumerateArray()
                .Select(attribute => attribute.GetString()).ToArray());
        return row;
    }

    private async Task AssertEnumerationCountAsync(DapTestClient client, string localName, int expected)
    {
        JsonElement[] fields = await ReadUnproxiedLocalAsync(client, localName).ConfigureAwait(false);
        JsonElement count = Assert.ContainsSingle(fields.Where(field =>
            field.GetProperty("name").GetString() == "_enumerationCount"));
        Assert.AreEqual(expected.ToString(System.Globalization.CultureInfo.InvariantCulture),
            count.GetProperty("value").GetString(), localName);
    }

    private async Task AssertStructEnumerationCountAsync(DapTestClient client, string localName, int expected)
    {
        JsonElement[] fields = await ReadUnproxiedLocalAsync(client, localName).ConfigureAwait(false);
        if (localName == "localResultsViewNullableStruct")
        {
            JsonElement value = Assert.ContainsSingle(fields.Where(field =>
                field.GetProperty("name").GetString() == "value"));
            fields = await ReadVariablesAsync(client, value.GetProperty("variablesReference").GetInt32())
                .ConfigureAwait(false);
        }

        JsonElement state = Assert.ContainsSingle(fields.Where(field =>
            field.GetProperty("name").GetString() == "_state"));
        JsonElement[] stateFields = await ReadVariablesAsync(
            client,
            state.GetProperty("variablesReference").GetInt32()).ConfigureAwait(false);
        JsonElement count = Assert.ContainsSingle(stateFields.Where(field =>
            field.GetProperty("name").GetString() == "_enumerationCount"));
        Assert.AreEqual(expected.ToString(System.Globalization.CultureInfo.InvariantCulture),
            count.GetProperty("value").GetString(), localName);
    }

    private async Task<JsonElement[]> ExpandResultsViewAsync(
        DapTestClient client,
        int reference,
        int? start = null,
        int? count = null)
    {
        JsonElement[] items = await ReadVariablesAsync(client, reference, start, count)
            .ConfigureAwait(false);
        await ReadResultsViewInvalidationAsync(client).ConfigureAwait(false);
        return items;
    }

    private async Task ReadResultsViewInvalidationAsync(DapTestClient client)
    {
        using JsonDocument invalidated = await client.ReadMessageAsync(TestContext.CancellationToken)
            .ConfigureAwait(false);
        AssertEvent(invalidated.RootElement, "invalidated");
        string?[] areas = [.. invalidated.RootElement.GetProperty("body").GetProperty("areas")
            .EnumerateArray().Select(area => area.GetString())];
        Assert.Contains("stacks", areas);
        Assert.Contains("variables", areas);
    }

    private async Task FinishResultsViewSessionAsync(DapTestClient client)
    {
        await DisconnectStoppedSessionAsync(client).ConfigureAwait(false);
        Assert.AreEqual(0, await client.WaitForExitAsync(TestContext.CancellationToken)
            .ConfigureAwait(false));
        Assert.AreEqual(string.Empty, client.Diagnostics.ToString());
    }
}
