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
            JsonElement snapshot = await ResolveResultsViewSnapshotAsync(
                client, finalRow.GetProperty("variablesReference").GetInt32()).ConfigureAwait(false);
            int snapshotReference = snapshot.GetProperty("variablesReference").GetInt32();
            Assert.AreEqual(3, snapshot.GetProperty("indexedVariables").GetInt32());
            Assert.AreEqual(0, snapshot.GetProperty("namedVariables").GetInt32());
            JsonElement snapshotFrame = await GetFixtureFrameAsync(client).ConfigureAwait(false);
            int snapshotFrameId = snapshotFrame.GetProperty("id").GetInt32();

            (int Start, int Count, string[] Names, string[] Values)[] pages =
            [
                (0, 1, ["[0]"], ["71"]),
                (1, 1, ["[1]"], ["72"]),
                (2, 4, ["[2]"], ["73"]),
                (3, 1, [], [])
            ];
            for (int index = 0; index < pages.Length; index++)
            {
                JsonElement[] items = await ReadResultsViewSnapshotPageAsync(
                    client,
                    snapshotReference,
                    pages[index].Start,
                    pages[index].Count,
                    filter: "indexed").ConfigureAwait(false);
                Assert.AreSequenceEqual(
                    pages[index].Names,
                    items.Select(item => item.GetProperty("name").GetString()).ToArray());
                Assert.AreSequenceEqual(
                    pages[index].Values,
                    items.Select(item => item.GetProperty("value").GetString()).ToArray());
                JsonElement counter = await ReadEvaluationAsync(
                    client, snapshotFrameId, "localResultsView._enumerationCount", success: true,
                    TestContext.CancellationToken).ConfigureAwait(false);
                Assert.AreEqual("1", counter.GetProperty("result").GetString());
            }

            Assert.IsEmpty(await ReadResultsViewSnapshotPageAsync(
                client, snapshotReference, 0, 0, filter: "named").ConfigureAwait(false));
            JsonElement[] all = await ReadResultsViewSnapshotPageAsync(
                client, snapshotReference, 0, 0).ConfigureAwait(false);
            Assert.AreSequenceEqual(
                ["71", "72", "73"],
                all.Select(item => item.GetProperty("value").GetString()).ToArray());
            await AssertEnumerationCountAsync(client, "localResultsView", 1)
                .ConfigureAwait(false);

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
            JsonElement snapshot = await ResolveResultsViewSnapshotAsync(
                client,
                row.GetProperty("variablesReference").GetInt32()).ConfigureAwait(false);
            int snapshotReference = snapshot.GetProperty("variablesReference").GetInt32();
            Assert.AreEqual(0, snapshot.GetProperty("indexedVariables").GetInt32());
            Assert.AreEqual(1, snapshot.GetProperty("namedVariables").GetInt32());
            JsonElement empty = Assert.ContainsSingle(await ReadResultsViewSnapshotPageAsync(
                client, snapshotReference, 0, 1, filter: "named").ConfigureAwait(false));
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
            Assert.IsEmpty(await ReadResultsViewSnapshotPageAsync(
                client, snapshotReference, 1, 1, filter: "named").ConfigureAwait(false));
            Assert.IsEmpty(await ReadResultsViewSnapshotPageAsync(
                client, snapshotReference, 0, 0, filter: "indexed").ConfigureAwait(false));
            await AssertEnumerationCountAsync(client, "localResultsViewEmpty", 1)
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

    /// <summary>
    /// Emits snapshot counts and applies paging only when the real client negotiates them.
    /// </summary>
    /// <param name="supportsVariablePaging">Whether the client advertises variable paging support.</param>
    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    [Timeout(30000, CooperativeCancellation = true)]
    public async Task ResultsViewSnapshotHonorsPagingNegotiation(bool supportsVariablePaging)
    {
        string waitPath = CreateResultsViewSignalPath();
        try
        {
            DapTestClient client = await StartProxyFixtureAsync(
                waitPath, supportsVariablePaging: supportsVariablePaging).ConfigureAwait(false);
            await using ConfiguredAsyncDisposable disposal = client.ConfigureAwait(false);
            JsonElement row = await ReadResultsViewRowAsync(client, "localResultsView")
                .ConfigureAwait(false);
            JsonElement snapshot = await ResolveResultsViewSnapshotAsync(
                client, row.GetProperty("variablesReference").GetInt32(), supportsVariablePaging)
                .ConfigureAwait(false);
            JsonElement[] page = await ReadResultsViewSnapshotPageAsync(
                client, snapshot.GetProperty("variablesReference").GetInt32(), 1, 1)
                .ConfigureAwait(false);
            string[] expectedNames = supportsVariablePaging ? ["[1]"] : ["[0]", "[1]", "[2]"];
            string[] expectedValues = supportsVariablePaging ? ["72"] : ["71", "72", "73"];
            Assert.AreSequenceEqual(
                expectedNames, page.Select(item => item.GetProperty("name").GetString()).ToArray());
            Assert.AreSequenceEqual(
                expectedValues, page.Select(item => item.GetProperty("value").GetString()).ToArray());
            await AssertEnumerationCountAsync(client, "localResultsView", 1).ConfigureAwait(false);
            await FinishResultsViewSessionAsync(client).ConfigureAwait(false);
        }
        finally
        {
            File.Delete(waitPath);
        }
    }

    /// <summary>
    /// Retires snapshots and their descendants after execution while allowing a fresh enumeration.
    /// </summary>
    [TestMethod]
    [Timeout(30000, CooperativeCancellation = true)]
    public async Task ResultsViewSnapshotRetiresAfterAnotherEnumeration()
    {
        string waitPath = CreateResultsViewSignalPath();
        try
        {
            DapTestClient client = await StartProxyFixtureAsync(waitPath).ConfigureAwait(false);
            await using ConfiguredAsyncDisposable disposal = client.ConfigureAwait(false);
            JsonElement row = await ReadResultsViewRowAsync(client, "localResultsViewInherited")
                .ConfigureAwait(false);
            JsonElement snapshot = await ResolveResultsViewSnapshotAsync(
                client, row.GetProperty("variablesReference").GetInt32()).ConfigureAwait(false);
            int snapshotReference = snapshot.GetProperty("variablesReference").GetInt32();
            JsonElement[] children = await ReadResultsViewSnapshotPageAsync(
                client, snapshotReference, 0, 0).ConfigureAwait(false);
            Assert.HasCount(2, children);
            int childReference = children[0].GetProperty("variablesReference").GetInt32();
            Assert.IsGreaterThan(0, childReference);
            JsonElement otherRow = await ReadResultsViewRowAsync(client, "localResultsView")
                .ConfigureAwait(false);
            JsonElement[] otherItems = await ExpandResultsViewAsync(
                client, otherRow.GetProperty("variablesReference").GetInt32()).ConfigureAwait(false);
            Assert.AreSequenceEqual(
                ["71", "72", "73"],
                otherItems.Select(item => item.GetProperty("value").GetString()).ToArray());
            int[] staleReferences = [snapshotReference, childReference];
            foreach (int staleReference in staleReferences)
            {
                int sequence = await client.SendRequestAsync(
                    "variables", writer => WriteResultsViewReference(writer, staleReference),
                    TestContext.CancellationToken).ConfigureAwait(false);
                using JsonDocument response = await client.ReadMessageAsync(TestContext.CancellationToken)
                    .ConfigureAwait(false);
                AssertResponse(response.RootElement, sequence, "variables", success: false);
                string? message = response.RootElement.GetProperty("message").GetString();
                Assert.IsNotNull(message);
                Assert.Contains("stale", message, StringComparison.OrdinalIgnoreCase);
            }

            await AssertEnumerationCountAsync(client, "localResultsViewInherited", 1)
                .ConfigureAwait(false);
            JsonElement freshRow = await ReadResultsViewRowAsync(client, "localResultsViewInherited")
                .ConfigureAwait(false);
            JsonElement[] freshItems = await ExpandResultsViewAsync(
                client, freshRow.GetProperty("variablesReference").GetInt32()).ConfigureAwait(false);
            Assert.HasCount(2, freshItems);
            await AssertEnumerationCountAsync(client, "localResultsViewInherited", 2)
                .ConfigureAwait(false);
            await FinishResultsViewSessionAsync(client).ConfigureAwait(false);
        }
        finally
        {
            File.Delete(waitPath);
        }
    }

    /// <summary>
    /// Rejects malformed lazy-variable filters before target execution or handle invalidation.
    /// </summary>
    /// <param name="filterJson">The unsupported filter encoded as a real JSON value.</param>
    [TestMethod]
    [DataRow("\"all\"")]
    [DataRow("\"\"")]
    [DataRow("null")]
    [DataRow("1")]
    [Timeout(30000, CooperativeCancellation = true)]
    public async Task ResultsViewRejectsMalformedFilterWithoutExecuting(string filterJson)
    {
        ArgumentException.ThrowIfNullOrEmpty(filterJson);
        string waitPath = CreateResultsViewSignalPath();
        try
        {
            DapTestClient client = await StartProxyFixtureAsync(waitPath).ConfigureAwait(false);
            await using ConfiguredAsyncDisposable disposal = client.ConfigureAwait(false);
            JsonElement row = await ReadResultsViewRowAsync(client, "localResultsView")
                .ConfigureAwait(false);
            int lazyReference = row.GetProperty("variablesReference").GetInt32();
            JsonElement frame = await GetFixtureFrameAsync(client).ConfigureAwait(false);
            int frameId = frame.GetProperty("id").GetInt32();
            using var filter = JsonDocument.Parse(filterJson);
            int sequence = await client.SendRequestAsync(
                "variables",
                writer =>
                {
                    writer.WriteStartObject();
                    writer.WriteNumber("variablesReference", lazyReference);
                    writer.WritePropertyName("filter");
                    filter.RootElement.WriteTo(writer);
                    writer.WriteEndObject();
                },
                TestContext.CancellationToken).ConfigureAwait(false);
            using JsonDocument response = await client.ReadMessageAsync(TestContext.CancellationToken)
                .ConfigureAwait(false);
            AssertResponse(response.RootElement, sequence, "variables", success: false);
            Assert.AreEqual("The variables filter must be 'named' or 'indexed'.",
                response.RootElement.GetProperty("message").GetString());
            JsonElement counter = await ReadEvaluationAsync(
                client, frameId, "localResultsView._enumerationCount", success: true,
                TestContext.CancellationToken).ConfigureAwait(false);
            Assert.AreEqual("0", counter.GetProperty("result").GetString());
            JsonElement[] items = await ExpandResultsViewAsync(client, lazyReference)
                .ConfigureAwait(false);
            Assert.AreSequenceEqual(
                ["71", "72", "73"],
                items.Select(item => item.GetProperty("value").GetString()).ToArray());
            await AssertEnumerationCountAsync(client, "localResultsView", 1).ConfigureAwait(false);
            await FinishResultsViewSessionAsync(client).ConfigureAwait(false);
        }
        finally
        {
            File.Delete(waitPath);
        }
    }

    /// <summary>
    /// Resolves one complete lazy replacement independently of child paging and filter arguments.
    /// </summary>
    /// <param name="filter">The valid child category supplied while resolving the lazy row.</param>
    /// <param name="start">The nonzero child offset that must not slice the replacement row.</param>
    [TestMethod]
    [DataRow("named", 1)]
    [DataRow("indexed", 4)]
    [Timeout(30000, CooperativeCancellation = true)]
    public async Task ResultsViewLazyResolutionIgnoresChildPagingAndFilter(string filter, int start)
    {
        string waitPath = CreateResultsViewSignalPath();
        try
        {
            DapTestClient client = await StartProxyFixtureAsync(waitPath).ConfigureAwait(false);
            await using ConfiguredAsyncDisposable disposal = client.ConfigureAwait(false);
            JsonElement row = await ReadResultsViewRowAsync(client, "localResultsView")
                .ConfigureAwait(false);
            await AssertEnumerationCountAsync(client, "localResultsView", 0).ConfigureAwait(false);
            JsonElement snapshot = await ResolveResultsViewSnapshotAsync(
                client, row.GetProperty("variablesReference").GetInt32(),
                start: start, count: 1, filter: filter).ConfigureAwait(false);
            Assert.AreEqual(3, snapshot.GetProperty("indexedVariables").GetInt32());
            Assert.AreEqual(0, snapshot.GetProperty("namedVariables").GetInt32());
            JsonElement first = Assert.ContainsSingle(await ReadResultsViewSnapshotPageAsync(
                client, snapshot.GetProperty("variablesReference").GetInt32(),
                0, 1, filter: "indexed").ConfigureAwait(false));
            Assert.AreEqual("[0]", first.GetProperty("name").GetString());
            Assert.AreEqual("71", first.GetProperty("value").GetString());
            await AssertEnumerationCountAsync(client, "localResultsView", 1).ConfigureAwait(false);
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
        JsonElement snapshot = await ResolveResultsViewSnapshotAsync(client, reference)
            .ConfigureAwait(false);
        return await ReadVariablesAsync(
            client, snapshot.GetProperty("variablesReference").GetInt32(), start, count)
            .ConfigureAwait(false);
    }

    private async Task<JsonElement> ResolveResultsViewSnapshotAsync(
        DapTestClient client,
        int lazyReference,
        bool supportsVariablePaging = true,
        int start = 0,
        int count = 0,
        string? filter = null)
    {
        JsonElement snapshot = Assert.ContainsSingle(await ReadResultsViewSnapshotPageAsync(
            client, lazyReference, start, count, filter).ConfigureAwait(false));
        Assert.AreEqual("Results View", snapshot.GetProperty("name").GetString());
        Assert.IsGreaterThan(0, snapshot.GetProperty("variablesReference").GetInt32());
        Assert.AreNotEqual(lazyReference, snapshot.GetProperty("variablesReference").GetInt32());
        Assert.IsFalse(snapshot.TryGetProperty("evaluateName", out _));
        JsonElement hint = snapshot.GetProperty("presentationHint");
        Assert.AreEqual("virtual", hint.GetProperty("kind").GetString());
        Assert.IsFalse(hint.TryGetProperty("lazy", out JsonElement lazy) && lazy.GetBoolean());
        Assert.AreSequenceEqual(
            ["readOnly"],
            hint.GetProperty("attributes").EnumerateArray()
                .Select(attribute => attribute.GetString()).ToArray());
        if (supportsVariablePaging)
        {
            Assert.IsGreaterThanOrEqualTo(0, snapshot.GetProperty("indexedVariables").GetInt32());
            Assert.IsGreaterThanOrEqualTo(0, snapshot.GetProperty("namedVariables").GetInt32());
        }
        else
        {
            Assert.IsFalse(snapshot.TryGetProperty("indexedVariables", out _));
            Assert.IsFalse(snapshot.TryGetProperty("namedVariables", out _));
        }

        await ReadResultsViewInvalidationAsync(client).ConfigureAwait(false);
        return snapshot;
    }

    private async Task<JsonElement[]> ReadResultsViewSnapshotPageAsync(
        DapTestClient client,
        int snapshotReference,
        int start,
        int count,
        string? filter = null)
    {
        int sequence = await client.SendRequestAsync(
            "variables",
            writer =>
            {
                writer.WriteStartObject();
                writer.WriteNumber("variablesReference", snapshotReference);
                writer.WriteNumber("start", start);
                writer.WriteNumber("count", count);
                if (filter is not null)
                {
                    writer.WriteString("filter", filter);
                }

                writer.WriteEndObject();
            },
            TestContext.CancellationToken).ConfigureAwait(false);
        using JsonDocument response = await client.ReadMessageAsync(TestContext.CancellationToken)
            .ConfigureAwait(false);
        AssertResponse(response.RootElement, sequence, "variables", success: true);
        return [.. response.RootElement.GetProperty("body").GetProperty("variables")
            .EnumerateArray().Select(variable => variable.Clone())];
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
