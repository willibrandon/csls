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

    private async Task<int> StopAtInitializedArraysAsync(DapTestClient client)
    {
        string path = Path.Join(FindRepositoryRoot(), "tests", "Csls.TestProcessHost", "DebuggerDumpArrayFixture.cs");
        int line = FindSourceLine(await File.ReadAllLinesAsync(path, TestContext.CancellationToken).ConfigureAwait(false),
            "DebuggerBlockingWait.Wait(announcement);");
        int initialize = await client.SendInitializeRequestAsync(TestContext.CancellationToken,
            writeProperties: writer => writer.WriteBoolean("supportsVariablePaging", true)).ConfigureAwait(false);
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
