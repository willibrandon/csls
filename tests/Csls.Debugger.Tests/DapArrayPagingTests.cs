using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Csls.Debugger.Tests;

/// <summary>
/// Inspects bounded pages of large live arrays through the real adapter and runtime.
/// </summary>
[TestClass]
public sealed class DapArrayPagingTests : DapTestContext
{
    /// <summary>
    /// Publishes exact array sizes for locals and nested elements when the client supports paging.
    /// </summary>
    /// <param name="paging">Whether the client advertises variable paging.</param>
    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    [Timeout(30000, CooperativeCancellation = true)]
    public async Task ArrayCountsDescribeLocalsAndNestedArrays(bool paging)
    {
        DapTestClient client = await DapTestClient.CreateAsync(TestContext.CancellationToken).ConfigureAwait(false);
        await using ConfiguredAsyncDisposable cleanup = client.ConfigureAwait(false);
        int frameId = await StopAtInitializedArraysAsync(client, paging).ConfigureAwait(false);
        int locals = await ReadLocalsReferenceAsync(client, frameId).ConfigureAwait(false);
        JsonElement[] variables = await ReadPageAsync(client, locals, 0, 0, "named").ConfigureAwait(false);
        foreach ((string name, int? length) in new (string, int?)[]
        {
            ("large", 65537), ("many", 65537), ("vector", 3), ("empty", 0),
            ("singleton", 1), ("rectangular", 6), ("nonZero", 6), ("nonVector", 2),
            ("emptyDimension", 0), ("absent", null)
        })
        {
            JsonElement variable = Assert.ContainsSingle(variables.Where(value =>
                value.GetProperty("name").GetString() == name));
            AssertChildCounts(variable, paging ? length : null);
        }

        JsonElement jagged = Assert.ContainsSingle(variables.Where(value =>
            value.GetProperty("name").GetString() == "jagged"));
        JsonElement[] elements = await ReadPageAsync(client,
            jagged.GetProperty("variablesReference").GetInt32(), 0, 0).ConfigureAwait(false);
        Assert.HasCount(4, elements);
        int[] lengths = [3, 1, 0, 3];
        for (int index = 0; index < elements.Length; index++)
        {
            Assert.AreEqual($"[{index}]", elements[index].GetProperty("name").GetString());
            AssertChildCounts(elements[index], paging ? lengths[index] : null);
        }
        await DisconnectAsync(client).ConfigureAwait(false);
    }

    /// <summary>
    /// Publishes evaluation sizes for populated, empty, multidimensional, and null arrays.
    /// </summary>
    /// <param name="paging">Whether the client advertises variable paging.</param>
    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    [Timeout(30000, CooperativeCancellation = true)]
    public async Task ArrayEvaluationReportsChildCounts(bool paging)
    {
        DapTestClient client = await DapTestClient.CreateAsync(TestContext.CancellationToken).ConfigureAwait(false);
        await using ConfiguredAsyncDisposable cleanup = client.ConfigureAwait(false);
        int frameId = await StopAtInitializedArraysAsync(client, paging).ConfigureAwait(false);
        foreach ((string expression, int? length) in new (string, int?)[]
        {
            ("large", 65537), ("empty", 0), ("singleton", 1), ("nonZero", 6),
            ("emptyDimension", 0), ("jagged[0]", 3), ("absent", null), ("large[0]", null)
        })
        {
            JsonElement result = await ReadEvaluationAsync(client, frameId, expression, success: true,
                TestContext.CancellationToken).ConfigureAwait(false);
            AssertChildCounts(result, paging ? length : null);
        }
        await DisconnectAsync(client).ConfigureAwait(false);
    }

    /// <summary>
    /// Reports the current array size after each physical variable or expression assignment.
    /// </summary>
    /// <param name="command">The DAP assignment operation.</param>
    /// <param name="paging">Whether the client advertises variable paging.</param>
    [TestMethod]
    [DataRow("setVariable", true)]
    [DataRow("setVariable", false)]
    [DataRow("setExpression", true)]
    [DataRow("setExpression", false)]
    [Timeout(30000, CooperativeCancellation = true)]
    public async Task ArrayAssignmentsReportReplacementCounts(string command, bool paging)
    {
        DapTestClient client = await DapTestClient.CreateAsync(TestContext.CancellationToken).ConfigureAwait(false);
        await using ConfiguredAsyncDisposable cleanup = client.ConfigureAwait(false);
        int frameId = await StopAtInitializedArraysAsync(client, paging).ConfigureAwait(false);
        int locals = await ReadLocalsReferenceAsync(client, frameId).ConfigureAwait(false);
        foreach ((string expression, int? length) in new (string, int?)[]
        {
            ("vector", 3), ("empty", 0), ("absent", null)
        })
        {
            int sequence = await client.SendRequestAsync(command, writer =>
            {
                writer.WriteStartObject();
                if (command == "setVariable")
                {
                    writer.WriteNumber("variablesReference", locals);
                    writer.WriteString("name", "large");
                }
                else
                {
                    writer.WriteNumber("frameId", frameId);
                    writer.WriteString("expression", "large");
                }
                writer.WriteString("value", expression);
                writer.WriteEndObject();
            }, TestContext.CancellationToken).ConfigureAwait(false);
            using JsonDocument response = await client.ReadMessageAsync(TestContext.CancellationToken).ConfigureAwait(false);
            AssertResponse(response.RootElement, sequence, command, success: true);
            JsonElement result = response.RootElement.GetProperty("body");
            AssertChildCounts(result, paging ? length : null);
            using JsonDocument invalidated = await client.ReadMessageAsync(TestContext.CancellationToken).ConfigureAwait(false);
            AssertEvent(invalidated.RootElement, "invalidated");
            if (length is int count)
            {
                JsonElement[] elements = await ReadPageAsync(client,
                    result.GetProperty("variablesReference").GetInt32(), 0, 0).ConfigureAwait(false);
                Assert.HasCount(count, elements);
                string[] expected = count == 0 ? [] : ["41", "42", "43"];
                Assert.AreSequenceEqual(expected,
                    elements.Select(value => value.GetProperty("value").GetString()));
            }
            else
            {
                Assert.AreEqual("null", result.GetProperty("value").GetString());
                Assert.AreEqual(0, result.GetProperty("variablesReference").GetInt32());
            }
        }
        await DisconnectAsync(client).ConfigureAwait(false);
    }

    /// <summary>
    /// Publishes array counts and usable pages from target-code function evaluation.
    /// </summary>
    /// <param name="expression">The invocation on a CLR array instance.</param>
    /// <param name="length">The number of elements preserved by the clone.</param>
    /// <param name="lastName">The last element's physical index, or null for an empty array.</param>
    /// <param name="lastValue">The last element's value, or null for an empty array.</param>
    [TestMethod]
    [DataRow("large.Clone()", 65537, "[65536]", "65636")]
    [DataRow("((int[])large).Clone()", 65537, "[65536]", "65636")]
    [DataRow("((System.Array)large).Clone()", 65537, "[65536]", "65636")]
    [DataRow("nonZero.Clone()", 6, "[-1,7]", "27")]
    [DataRow("empty.Clone()", 0, null, null)]
    [Timeout(30000, CooperativeCancellation = true)]
    public async Task FunctionEvaluationReportsArrayCounts(string expression, int length, string? lastName, string? lastValue)
    {
        DapTestClient client = await DapTestClient.CreateAsync(TestContext.CancellationToken).ConfigureAwait(false);
        await using ConfiguredAsyncDisposable cleanup = client.ConfigureAwait(false);
        int frameId = await StopAtInitializedArraysAsync(client).ConfigureAwait(false);
        JsonElement result = await ReadEvaluationAsync(client, frameId, expression, success: true,
            TestContext.CancellationToken).ConfigureAwait(false);
        AssertChildCounts(result, length);
        using JsonDocument invalidated = await client.ReadMessageAsync(TestContext.CancellationToken).ConfigureAwait(false);
        AssertEvent(invalidated.RootElement, "invalidated");
        JsonElement[] page = await ReadPageAsync(client,
            result.GetProperty("variablesReference").GetInt32(), Math.Max(0, length - 1), 1).ConfigureAwait(false);
        if (length == 0)
        {
            Assert.IsEmpty(page);
        }
        else
        {
            JsonElement last = Assert.ContainsSingle(page);
            Assert.AreEqual(lastName, last.GetProperty("name").GetString());
            Assert.AreEqual(lastValue, last.GetProperty("value").GetString());
        }
        await DisconnectAsync(client).ConfigureAwait(false);
    }

    /// <summary>
    /// Materializes a large enumerable once and retrieves bounded pages from its retained array.
    /// </summary>
    [TestMethod]
    [Timeout(30000, CooperativeCancellation = true)]
    public async Task LargeResultsViewReportsCountAndServesPages()
    {
        DapTestClient client = await DapTestClient.CreateAsync(TestContext.CancellationToken).ConfigureAwait(false);
        await using ConfiguredAsyncDisposable cleanup = client.ConfigureAwait(false);
        int frameId = await StopAtInitializedArraysAsync(client).ConfigureAwait(false);
        JsonElement enumerable = await ReadEvaluationAsync(client, frameId, "results", success: true,
            TestContext.CancellationToken).ConfigureAwait(false);
        JsonElement[] fields = await ReadPageAsync(client,
            enumerable.GetProperty("variablesReference").GetInt32(), 0, 0, "named").ConfigureAwait(false);
        JsonElement row = Assert.ContainsSingle(fields.Where(value =>
            value.GetProperty("name").GetString() == "Results View"));
        Assert.IsTrue(row.GetProperty("presentationHint").GetProperty("lazy").GetBoolean());
        JsonElement before = await ReadEvaluationAsync(client, frameId, "results._enumerationCount", success: true,
            TestContext.CancellationToken).ConfigureAwait(false);
        Assert.AreEqual("0", before.GetProperty("result").GetString());
        JsonElement snapshot = Assert.ContainsSingle(await ReadPageAsync(client,
            row.GetProperty("variablesReference").GetInt32(), 0, 0, "named").ConfigureAwait(false));
        Assert.AreEqual("Results View", snapshot.GetProperty("name").GetString());
        AssertChildCounts(snapshot, 65537);
        using JsonDocument invalidated = await client.ReadMessageAsync(TestContext.CancellationToken).ConfigureAwait(false);
        AssertEvent(invalidated.RootElement, "invalidated");
        int reference = snapshot.GetProperty("variablesReference").GetInt32();
        Assert.AreNotEqual(row.GetProperty("variablesReference").GetInt32(), reference);
        foreach (int start in new[] { 0, 65536 })
        {
            JsonElement item = Assert.ContainsSingle(await ReadPageAsync(client, reference, start, 1)
                .ConfigureAwait(false));
            Assert.AreEqual($"[{start}]", item.GetProperty("name").GetString());
            Assert.AreEqual((start + 100).ToString(CultureInfo.InvariantCulture), item.GetProperty("value").GetString());
        }
        JsonElement rejected = await RequestPageAsync(client, reference, 0, 0, success: false).ConfigureAwait(false);
        Assert.Contains("page", Assert.IsInstanceOfType<string>(rejected.GetProperty("message").GetString()));
        Assert.IsEmpty(await ReadPageAsync(client, reference, 65537, 1).ConfigureAwait(false));
        int sequence = await client.SendRequestAsync("evaluate", writer =>
        {
            writer.WriteStartObject();
            writer.WriteString("expression", "results._enumerationCount");
            writer.WriteEndObject();
        }, TestContext.CancellationToken).ConfigureAwait(false);
        using JsonDocument after = await client.ReadMessageAsync(TestContext.CancellationToken).ConfigureAwait(false);
        AssertResponse(after.RootElement, sequence, "evaluate", success: true);
        Assert.AreEqual("1", after.RootElement.GetProperty("body").GetProperty("result").GetString());
        await DisconnectAsync(client).ConfigureAwait(false);
    }

    /// <summary>
    /// Reads scalar and expandable elements beyond the page budget while preserving session usability after rejection.
    /// </summary>
    /// <param name="expression">The initialized large array retained by the stopped fixture.</param>
    /// <param name="expandable">Whether the selected elements contain nested arrays.</param>
    [TestMethod]
    [DataRow("large", false)]
    [DataRow("many", true)]
    [Timeout(30000, CooperativeCancellation = true)]
    public async Task LargeArraysServeBoundedPages(string expression, bool expandable)
    {
        DapTestClient client = await DapTestClient.CreateAsync(TestContext.CancellationToken).ConfigureAwait(false);
        await using ConfiguredAsyncDisposable cleanup = client.ConfigureAwait(false);
        int frameId = await StopAtInitializedArraysAsync(client).ConfigureAwait(false);
        JsonElement array = await ReadEvaluationAsync(client, frameId, expression, success: true,
            TestContext.CancellationToken).ConfigureAwait(false);
        Assert.AreEqual(expandable ? "int[][]" : "int[]", array.GetProperty("type").GetString());
        int reference = array.GetProperty("variablesReference").GetInt32();
        Assert.IsGreaterThan(0, reference);

        (int Start, int Count, int Length)[] ranges =
        [
            (0, 1, 1), (65535, 1, 1), (65535, int.MaxValue, 2),
            (65536, 1, 1), (65536, 0, 1), (65536, int.MaxValue, 1),
            (65537, 0, 0), (65537, int.MaxValue, 0), (int.MaxValue, int.MaxValue, 0)
        ];
        foreach ((int start, int count, int length) in ranges)
        {
            JsonElement[] page = await ReadPageAsync(client, reference, start, count).ConfigureAwait(false);
            Assert.HasCount(length, page);
            for (int index = 0; index < length; index++)
            {
                JsonElement element = page[index];
                Assert.AreEqual($"[{start + index}]", element.GetProperty("name").GetString());
                Assert.AreEqual($"{expression}[{start + index}]", element.GetProperty("evaluateName").GetString());
                if (expandable)
                {
                    Assert.AreEqual("int[]", element.GetProperty("type").GetString());
                    int child = element.GetProperty("variablesReference").GetInt32();
                    Assert.IsGreaterThan(0, child);
                    JsonElement[] values = await ReadPageAsync(client, child, 0, 0).ConfigureAwait(false);
                    Assert.AreSequenceEqual(["41", "42", "43"],
                        values.Select(value => value.GetProperty("value").GetString()));
                }
                else
                {
                    Assert.AreEqual("int", element.GetProperty("type").GetString());
                    Assert.AreEqual((start + index + 100).ToString(CultureInfo.InvariantCulture),
                        element.GetProperty("value").GetString());
                    Assert.AreEqual(0, element.GetProperty("variablesReference").GetInt32());
                }
            }
        }

        Assert.IsEmpty(await ReadPageAsync(client, reference, 0, int.MaxValue, filter: "named").ConfigureAwait(false));
        foreach (int count in new[] { 0, 65537, int.MaxValue })
        {
            JsonElement rejected = await RequestPageAsync(client, reference, 0, count, success: false)
                .ConfigureAwait(false);
            Assert.Contains("page", Assert.IsInstanceOfType<string>(rejected.GetProperty("message").GetString()));
            Assert.Contains("65536", Assert.IsInstanceOfType<string>(rejected.GetProperty("message").GetString()));
            JsonElement[] last = await ReadPageAsync(client, reference, 65536, 1).ConfigureAwait(false);
            Assert.AreEqual("[65536]", Assert.ContainsSingle(last).GetProperty("name").GetString());
        }

        JsonElement scalar = await ReadEvaluationAsync(client, frameId, "large[65536]", success: true,
            TestContext.CancellationToken).ConfigureAwait(false);
        Assert.AreEqual("65636", scalar.GetProperty("result").GetString());
        await DisconnectAsync(client).ConfigureAwait(false);
    }

    /// <summary>
    /// Applies the response bound after clamping the requested range to the actual array length.
    /// </summary>
    [TestMethod]
    [Timeout(30000, CooperativeCancellation = true)]
    public async Task ArrayPageBudgetBoundsReturnedElements()
    {
        DapTestClient client = await DapTestClient.CreateAsync(TestContext.CancellationToken).ConfigureAwait(false);
        await using ConfiguredAsyncDisposable cleanup = client.ConfigureAwait(false);
        int frameId = await StopAtInitializedArraysAsync(client).ConfigureAwait(false);
        JsonElement array = await ReadEvaluationAsync(client, frameId, "large", success: true,
            TestContext.CancellationToken).ConfigureAwait(false);
        int reference = array.GetProperty("variablesReference").GetInt32();
        foreach (int start in new[] { 2, 1 })
        {
            JsonElement[] page = await ReadPageAsync(client, reference, start, int.MaxValue).ConfigureAwait(false);
            Assert.HasCount(65537 - start, page);
            for (int index = 0; index < page.Length; index++)
            {
                Assert.AreEqual($"[{start + index}]", page[index].GetProperty("name").GetString());
                Assert.AreEqual((start + index + 100).ToString(CultureInfo.InvariantCulture),
                    page[index].GetProperty("value").GetString());
            }
        }

        await DisconnectAsync(client).ConfigureAwait(false);
    }

    /// <summary>
    /// Preserves empty arrays and physical multidimensional or nonzero lower-bound indices when paging.
    /// </summary>
    /// <param name="expression">The initialized array retained by the stopped fixture.</param>
    /// <param name="start">The first linear element to inspect.</param>
    /// <param name="count">The requested maximum page length.</param>
    /// <param name="names">The independent physical index names expected in the page.</param>
    /// <param name="values">The fixture values expected at those indices.</param>
    [TestMethod]
    [DataRow("empty", 0, 0, new string[0], new string[0])]
    [DataRow("emptyDimension", 0, int.MaxValue, new string[0], new string[0])]
    [DataRow("singleton", 0, int.MaxValue, new[] { "[0]" }, new[] { "91" })]
    [DataRow("nonVector", 1, int.MaxValue, new[] { "[-2]" }, new[] { "72" })]
    [DataRow("nonZero", 1, 3, new[] { "[-2,6]", "[-2,7]", "[-1,5]" }, new[] { "16", "17", "25" })]
    [DataRow("rectangular", 2, 3, new[] { "[0,2]", "[1,0]", "[1,1]" }, new[] { "13", "21", "22" })]
    [Timeout(30000, CooperativeCancellation = true)]
    public async Task ArrayPagesPreserveRuntimeIndices(string expression, int start, int count,
        string[] names, string[] values)
    {
        ArgumentNullException.ThrowIfNull(names);
        ArgumentNullException.ThrowIfNull(values);
        DapTestClient client = await DapTestClient.CreateAsync(TestContext.CancellationToken).ConfigureAwait(false);
        await using ConfiguredAsyncDisposable cleanup = client.ConfigureAwait(false);
        int frameId = await StopAtInitializedArraysAsync(client).ConfigureAwait(false);
        JsonElement array = await ReadEvaluationAsync(client, frameId, expression, success: true,
            TestContext.CancellationToken).ConfigureAwait(false);
        int reference = array.GetProperty("variablesReference").GetInt32();
        Assert.IsGreaterThan(0, reference);
        JsonElement[] page = await ReadPageAsync(client, reference, start, count).ConfigureAwait(false);
        Assert.AreSequenceEqual(names, page.Select(value => value.GetProperty("name").GetString()));
        Assert.AreSequenceEqual(values, page.Select(value => value.GetProperty("value").GetString()));
        await DisconnectAsync(client).ConfigureAwait(false);
    }

    private static void AssertChildCounts(JsonElement value, int? length)
    {
        if (length is int count)
        {
            Assert.IsTrue(value.TryGetProperty("namedVariables", out JsonElement named), value.GetRawText());
            Assert.AreEqual(0, named.GetInt32());
            Assert.IsTrue(value.TryGetProperty("indexedVariables", out JsonElement indexed), value.GetRawText());
            Assert.AreEqual(count, indexed.GetInt32());
        }
        else
        {
            Assert.IsFalse(value.TryGetProperty("namedVariables", out _), value.GetRawText());
            Assert.IsFalse(value.TryGetProperty("indexedVariables", out _), value.GetRawText());
        }
    }

    private async Task<int> ReadLocalsReferenceAsync(DapTestClient client, int frameId)
    {
        int sequence = await client.SendRequestAsync("scopes", writer =>
        {
            writer.WriteStartObject();
            writer.WriteNumber("frameId", frameId);
            writer.WriteEndObject();
        }, TestContext.CancellationToken).ConfigureAwait(false);
        using JsonDocument response = await client.ReadMessageAsync(TestContext.CancellationToken).ConfigureAwait(false);
        AssertResponse(response.RootElement, sequence, "scopes", success: true);
        return Assert.ContainsSingle(response.RootElement.GetProperty("body").GetProperty("scopes")
            .EnumerateArray().Where(scope => scope.GetProperty("name").GetString() == "Locals"))
            .GetProperty("variablesReference").GetInt32();
    }

    private async Task<int> StopAtInitializedArraysAsync(DapTestClient client, bool paging = true)
    {
        string path = Path.Join(FindRepositoryRoot(), "tests", "Csls.TestProcessHost", "DebuggerDumpArrayFixture.cs");
        int line = FindSourceLine(await File.ReadAllLinesAsync(path, TestContext.CancellationToken).ConfigureAwait(false),
            "DebuggerBlockingWait.Wait(announcement);");
        int initialize = await client.SendInitializeRequestAsync(TestContext.CancellationToken,
            writeProperties: writer => writer.WriteBoolean("supportsVariablePaging", paging)).ConfigureAwait(false);
        using (JsonDocument response = await client.ReadMessageAsync(TestContext.CancellationToken).ConfigureAwait(false))
        {
            AssertResponse(response.RootElement, initialize, "initialize", success: true);
        }

        int launch = await client.SendRequestAsync("launch", writer => WriteLaunchArguments(writer,
            ResolveTestProcessHost(), ["--debugger-dump-arrays", nameof(DapArrayPagingTests)], wait: true, noDebug: false),
            TestContext.CancellationToken).ConfigureAwait(false);
        using (JsonDocument initialized = await client.ReadMessageAsync(TestContext.CancellationToken).ConfigureAwait(false))
        {
            AssertEvent(initialized.RootElement, "initialized");
        }

        int breakpoint = await client.SendRequestAsync("setBreakpoints",
            writer => WriteSourceBreakpointArguments(writer, path, line), TestContext.CancellationToken).ConfigureAwait(false);
        using (JsonDocument response = await client.ReadMessageAsync(TestContext.CancellationToken).ConfigureAwait(false))
        {
            AssertResponse(response.RootElement, breakpoint, "setBreakpoints", success: true);
        }

        int configuration = await client.SendRequestAsync("configurationDone", WriteEmptyObject,
            TestContext.CancellationToken).ConfigureAwait(false);
        int threadId = await ReadInitialBreakpointStopAsync(client, configuration, launch,
            TestContext.CancellationToken, TestContext).ConfigureAwait(false);
        JsonElement stack = await ReadDeepStackPageAsync(client, threadId, 0, 1).ConfigureAwait(false);
        JsonElement frame = Assert.ContainsSingle(stack.GetProperty("stackFrames").EnumerateArray());
        Assert.AreEqual("Csls.TestProcessHost.DebuggerDumpArrayFixture.Run", frame.GetProperty("name").GetString());
        Assert.AreEqual(line, frame.GetProperty("line").GetInt32());
        return frame.GetProperty("id").GetInt32();
    }

    private async Task<JsonElement[]> ReadPageAsync(DapTestClient client, int reference, int start, int count,
        string filter = "indexed")
    {
        JsonElement response = await RequestPageAsync(client, reference, start, count, success: true, filter)
            .ConfigureAwait(false);
        return [.. response.GetProperty("body").GetProperty("variables").EnumerateArray()];
    }

    private async Task<JsonElement> RequestPageAsync(DapTestClient client, int reference, int start, int count,
        bool success, string filter = "indexed")
    {
        int sequence = await client.SendRequestAsync("variables", writer =>
        {
            writer.WriteStartObject();
            writer.WriteNumber("variablesReference", reference);
            writer.WriteNumber("start", start);
            writer.WriteNumber("count", count);
            writer.WriteString("filter", filter);
            writer.WriteEndObject();
        }, TestContext.CancellationToken).ConfigureAwait(false);
        using JsonDocument response = await client.ReadMessageAsync(TestContext.CancellationToken).ConfigureAwait(false);
        AssertResponse(response.RootElement, sequence, "variables", success);
        return response.RootElement.Clone();
    }
}
