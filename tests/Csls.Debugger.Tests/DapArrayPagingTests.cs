using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Csls.Debugger.Tests;

/// <summary>
/// Inspects bounded pages of large live arrays through the real adapter and runtime.
/// </summary>
[TestClass]
public sealed partial class DapArrayPagingTests : DapTestContext
{
    /// <summary>
    /// Completes the pending evaluation and terminal sequence after abrupt target death.
    /// </summary>
    [TestMethod]
    [Timeout(30000, CooperativeCancellation = true)]
    public async Task AbruptTargetDeathDuringEvaluationCompletesPendingRequest()
    {
        DapTestClient client = await DapTestClient.CreateAsync(TestContext.CancellationToken).ConfigureAwait(false);
        await using ConfiguredAsyncDisposable cleanup = client.ConfigureAwait(false);
        using DapTestCancellationCapture capture = CaptureProtocolOnCancellation(client);
        int frameId = await StopAtInitializedArraysAsync(client).ConfigureAwait(false);
        int targetId = Assert.IsInstanceOfType<int>(client.TargetProcessId);
        using var target = Process.GetProcessById(targetId);
        _ = target.SafeHandle;

        int sequence = await client.SendRequestAsync("evaluate", writer =>
        {
            writer.WriteStartObject();
            writer.WriteString("expression", "Csls.TestProcessHost.DebuggerDumpArrayFixture.CrashDuringDebuggerEvaluation()");
            writer.WriteNumber("frameId", frameId);
            writer.WriteString("context", "watch");
            writer.WriteEndObject();
        }, TestContext.CancellationToken).ConfigureAwait(false);
        bool responseReceived = false;
        bool exitedReceived = false;
        bool terminatedReceived = false;
        bool evaluationEntered = false;
        while (!responseReceived || !terminatedReceived)
        {
            using JsonDocument message = await client.ReadMessageAsync(TestContext.CancellationToken)
                .ConfigureAwait(false);
            JsonElement root = message.RootElement;
            if (root.GetProperty("type").GetString() == "response")
            {
                Assert.IsFalse(responseReceived);
                AssertResponse(root, sequence, "evaluate", success: false);
                string failure = Assert.IsInstanceOfType<string>(root.GetProperty("message").GetString());
                Assert.Contains("target exited", failure, StringComparison.OrdinalIgnoreCase);
                responseReceived = true;
                continue;
            }

            switch (root.GetProperty("event").GetString())
            {
                case "output":
                    evaluationEntered |= root.GetProperty("body").GetProperty("output")
                        .GetString()?.Contains("csls-evaluation-crash-entered", StringComparison.Ordinal) == true;
                    break;
                case "exited":
                    Assert.IsFalse(exitedReceived);
                    Assert.AreNotEqual(0, root.GetProperty("body").GetProperty("exitCode").GetInt32());
                    exitedReceived = true;
                    break;
                case "terminated":
                    Assert.IsFalse(terminatedReceived);
                    Assert.IsTrue(exitedReceived);
                    terminatedReceived = true;
                    break;
                default:
                    Assert.Fail($"Unexpected event after abrupt target death: {root.GetRawText()}");
                    break;
            }
        }

        Assert.IsTrue(evaluationEntered);
        await target.WaitForExitAsync(TestContext.CancellationToken).ConfigureAwait(false);
        Assert.AreEqual(0, await client.WaitForExitAsync(TestContext.CancellationToken).ConfigureAwait(false));
        Assert.IsEmpty(client.Diagnostics.ToString());
    }

    /// <summary>
    /// Completes a pending evaluation and releases its owned target when target code requests shutdown.
    /// </summary>
    [TestMethod]
    [Timeout(30000, CooperativeCancellation = true)]
    public async Task EnvironmentExitDuringEvaluationCompletesAndCleansUpTarget()
    {
        DapTestClient client = await DapTestClient.CreateAsync(TestContext.CancellationToken).ConfigureAwait(false);
        await using ConfiguredAsyncDisposable cleanup = client.ConfigureAwait(false);
        using DapTestCancellationCapture capture = CaptureProtocolOnCancellation(client);
        int frameId = await StopAtInitializedArraysAsync(client).ConfigureAwait(false);
        int targetId = Assert.IsInstanceOfType<int>(client.TargetProcessId);
        using var target = Process.GetProcessById(targetId);
        _ = target.SafeHandle;

        int sequence = await client.SendRequestAsync("evaluate", writer =>
        {
            writer.WriteStartObject();
            writer.WriteString("expression", "Csls.TestProcessHost.DebuggerDumpArrayFixture.ExitDuringDebuggerEvaluation()");
            writer.WriteNumber("frameId", frameId);
            writer.WriteString("context", "watch");
            writer.WriteEndObject();
        }, TestContext.CancellationToken).ConfigureAwait(false);
        bool responseReceived = false;
        bool exitedReceived = false;
        bool terminatedReceived = false;
        bool evaluationEntered = false;
        while (!responseReceived)
        {
            using JsonDocument message = await client.ReadMessageAsync(TestContext.CancellationToken)
                .ConfigureAwait(false);
            JsonElement root = message.RootElement;
            if (root.GetProperty("type").GetString() == "response")
            {
                Assert.IsFalse(responseReceived);
                AssertResponse(root, sequence, "evaluate", success: false);
                string failure = Assert.IsInstanceOfType<string>(root.GetProperty("message").GetString());
                Assert.Contains("evaluation", failure, StringComparison.OrdinalIgnoreCase);
                responseReceived = true;
                continue;
            }

            switch (root.GetProperty("event").GetString())
            {
                case "output":
                    evaluationEntered |= root.GetProperty("body").GetProperty("output")
                        .GetString()?.Contains("csls-evaluation-exit-entered", StringComparison.Ordinal) == true;
                    break;
                case "exited":
                    Assert.IsFalse(exitedReceived);
                    Assert.AreEqual(37, root.GetProperty("body").GetProperty("exitCode").GetInt32());
                    exitedReceived = true;
                    break;
                case "terminated":
                    Assert.IsFalse(terminatedReceived);
                    Assert.IsTrue(exitedReceived);
                    terminatedReceived = true;
                    break;
                default:
                    Assert.Fail($"Unexpected event while the target exited: {root.GetRawText()}");
                    break;
            }
        }

        Assert.IsTrue(evaluationEntered);
        if (terminatedReceived)
        {
            Assert.IsTrue(exitedReceived);
        }
        await DisconnectAsync(client).ConfigureAwait(false);
        await target.WaitForExitAsync(TestContext.CancellationToken).ConfigureAwait(false);
        Assert.IsEmpty(client.Diagnostics.ToString());
    }

    /// <summary>
    /// Keeps automatic editor inspection from executing a target method.
    /// </summary>
    [TestMethod]
    [Timeout(30000, CooperativeCancellation = true)]
    public async Task NonInteractiveEvaluateContextsDoNotExecuteTargetCode()
    {
        DapTestClient client = await DapTestClient.CreateAsync(TestContext.CancellationToken).ConfigureAwait(false);
        await using ConfiguredAsyncDisposable cleanup = client.ConfigureAwait(false);
        using DapTestCancellationCapture capture = CaptureProtocolOnCancellation(client);
        int frameId = await StopAtInitializedArraysAsync(client).ConfigureAwait(false);
        foreach (string? context in new string?[] { "hover", "clipboard", "variables", null })
        {
            foreach (string targetCodeExpression in new[]
            {
                "Csls.TestProcessHost.DebuggerDumpArrayFixture.CrashDuringDebuggerEvaluation()",
                "(double)implicitConversion"
            })
            {
                int sequence = await client.SendRequestAsync("evaluate", writer =>
                {
                    writer.WriteStartObject();
                    writer.WriteString("expression", targetCodeExpression);
                    writer.WriteNumber("frameId", frameId);
                    if (context is not null)
                    {
                        writer.WriteString("context", context);
                    }

                    writer.WriteEndObject();
                }, TestContext.CancellationToken).ConfigureAwait(false);
                using JsonDocument response = await client.ReadMessageAsync(TestContext.CancellationToken)
                    .ConfigureAwait(false);
                AssertResponse(response.RootElement, sequence, "evaluate", success: false);
                Assert.Contains("not authorized", Assert.IsInstanceOfType<string>(
                    response.RootElement.GetProperty("message").GetString()));
            }

            int inspection = await client.SendRequestAsync("evaluate", writer =>
            {
                writer.WriteStartObject();
                writer.WriteString("expression", "vector[0]");
                writer.WriteNumber("frameId", frameId);
                if (context is not null)
                {
                    writer.WriteString("context", context);
                }

                writer.WriteEndObject();
            }, TestContext.CancellationToken).ConfigureAwait(false);
            using JsonDocument inspected = await client.ReadMessageAsync(TestContext.CancellationToken)
                .ConfigureAwait(false);
            AssertResponse(inspected.RootElement, inspection, "evaluate", success: true);
            Assert.AreEqual("41", inspected.RootElement.GetProperty("body").GetProperty("result").GetString());
        }

        await DisconnectAsync(client).ConfigureAwait(false);
        Assert.IsEmpty(client.Diagnostics.ToString());
    }


    /// <summary>
    /// Selects a numeric overload and passes a CLR value widened to its parameter type.
    /// </summary>
    [TestMethod]
    [Timeout(30000, CooperativeCancellation = true)]
    public async Task NumericWideningSelectsAndMarshalsParameterType()
    {
        DapTestClient client = await DapTestClient.CreateAsync(TestContext.CancellationToken).ConfigureAwait(false);
        await using ConfiguredAsyncDisposable cleanup = client.ConfigureAwait(false);
        int frameId = await StopAtInitializedArraysAsync(client).ConfigureAwait(false);
        const string receiver = "Csls.TestProcessHost.DebuggerDumpArrayFixture";
        foreach ((string expression, string expected) in new[]
        {
            ($"{receiver}.SelectWidenedForDebugger(vector[0])", "1041"),
            ($"{receiver}.CompilerSelectWidenedForDebugger()", "1041"),
            ($"{receiver}.SelectWidenedForDebugger((float)vector[0])", "2041"),
            ($"{receiver}.SelectWidenedForDebugger((double)vector[0])", "2041"),
            ($"{receiver}.SelectWidenedForDebugger((decimal)vector[0])", "3041"),
            ($"{receiver}.SelectSignedForDebugger((byte)vector[0])", "141"),
            ($"{receiver}.CompilerSelectSignedForDebugger()", "141"),
            ($"{receiver}.SelectNativeForDebugger((byte)vector[0])", "3041"),
            ($"{receiver}.CompilerSelectNativeForDebugger()", "3041"),
            ($"{receiver}.SelectNativeUnsignedForDebugger((uint)vector[0])", "5041"),
            ($"{receiver}.CompilerSelectNativeUnsignedForDebugger()", "5041"),
            ($"{receiver}.RequireByteForDebugger(41)", "7041"),
            ($"{receiver}.CompilerRequireByteForDebugger()", "7041"),
            ($"{receiver}.SelectConstantForDebugger(41)", "9041"),
            ($"{receiver}.CompilerSelectConstantForDebugger()", "9041"),
            ($"{receiver}.RequireUlongForDebugger(41L)", "8041"),
            ($"{receiver}.CompilerRequireUlongForDebugger()", "8041")
        })
        {
            JsonElement value = await ReadEvaluationAsync(client, frameId, expression, success: true,
                TestContext.CancellationToken).ConfigureAwait(false);
            Assert.AreEqual(expected, value.GetProperty("result").GetString());
            using JsonDocument invalidated = await client.ReadMessageAsync(TestContext.CancellationToken)
                .ConfigureAwait(false);
            AssertEvent(invalidated.RootElement, "invalidated");
        }

        JsonElement rejected = await ReadEvaluationAsync(client, frameId,
            $"{receiver}.RequireUnsignedForDebugger(vector[0])", success: false,
            TestContext.CancellationToken).ConfigureAwait(false);
        string message = Assert.IsInstanceOfType<string>(rejected.GetProperty("message").GetString());
        Assert.Contains("No static method", message);
        JsonElement outOfRange = await ReadEvaluationAsync(client, frameId,
            $"{receiver}.RequireByteForDebugger(256)", success: false,
            TestContext.CancellationToken).ConfigureAwait(false);
        Assert.Contains("No static method", Assert.IsInstanceOfType<string>(
            outOfRange.GetProperty("message").GetString()));
        JsonElement stillStopped = await ReadEvaluationAsync(client, frameId, "vector[0]", success: true,
            TestContext.CancellationToken).ConfigureAwait(false);
        Assert.AreEqual("41", stillStopped.GetProperty("result").GetString());
        await DisconnectAsync(client).ConfigureAwait(false);
    }

    /// <summary>
    /// Binds named source arguments to loaded static, instance, and constructor parameters.
    /// </summary>
    [TestMethod]
    [Timeout(30000, CooperativeCancellation = true)]
    public async Task NamedArgumentsFollowLoadedParameterNamesAndClrOrder()
    {
        DapTestClient client = await DapTestClient.CreateAsync(TestContext.CancellationToken).ConfigureAwait(false);
        await using ConfiguredAsyncDisposable cleanup = client.ConfigureAwait(false);
        using DapTestCancellationCapture capture = CaptureProtocolOnCancellation(client);
        int frameId = await StopAtInitializedArraysAsync(client).ConfigureAwait(false);
        const string staticReceiver = "Csls.TestProcessHost.DebuggerDumpArrayFixture";
        foreach ((string expression, string expected) in new[]
        {
            ($"{staticReceiver}.CombineNamedForDebugger(second: vector[1], first: vector[0])", "4142"),
            ($"{staticReceiver}.CombineNamedForDebugger(first: vector[0], vector[1])", "4142"),
            ($"{staticReceiver}.CombineThreeForDebugger(first: vector[0], second: vector[1], vector[2])", "414243"),
            ("capturedObject.CombineNamedForDebugger(second: vector[1], first: vector[0])", "4184")
        })
        {
            JsonElement value = await ReadEvaluationAsync(client, frameId, expression, success: true,
                TestContext.CancellationToken).ConfigureAwait(false);
            Assert.AreEqual(expected, value.GetProperty("result").GetString());
            using JsonDocument invalidated = await client.ReadMessageAsync(TestContext.CancellationToken)
                .ConfigureAwait(false);
            AssertEvent(invalidated.RootElement, "invalidated");
        }

        JsonElement created = await ReadEvaluationAsync(client, frameId,
            "new Csls.TestProcessHost.DebuggerFixtureValue(text: path, " +
            "evaluationSignalPath: path, number: vector[0])", success: true,
            TestContext.CancellationToken).ConfigureAwait(false);
        using (JsonDocument invalidated = await client.ReadMessageAsync(TestContext.CancellationToken)
            .ConfigureAwait(false))
        {
            AssertEvent(invalidated.RootElement, "invalidated");
        }

        JsonElement[] fields = await ReadPageAsync(client,
            created.GetProperty("variablesReference").GetInt32(), 0, 0, "named").ConfigureAwait(false);
        JsonElement number = Assert.ContainsSingle(fields.Where(field =>
            field.GetProperty("name").GetString() == "Number"));
        Assert.AreEqual("41", number.GetProperty("value").GetString());

        JsonElement rejected = await ReadEvaluationAsync(client, frameId,
            $"{staticReceiver}.CombineNamedForDebugger(unknown: vector[1], first: vector[0])",
            success: false, TestContext.CancellationToken).ConfigureAwait(false);
        Assert.Contains("No static method", Assert.IsInstanceOfType<string>(
            rejected.GetProperty("message").GetString()));
        JsonElement outOfPosition = await ReadEvaluationAsync(client, frameId,
            $"{staticReceiver}.CombineThreeForDebugger(second: 42, first: 41, 43)",
            success: false, TestContext.CancellationToken).ConfigureAwait(false);
        Assert.Contains("No static method", Assert.IsInstanceOfType<string>(
            outOfPosition.GetProperty("message").GetString()));
        JsonElement stillStopped = await ReadEvaluationAsync(client, frameId, "vector[0]", success: true,
            TestContext.CancellationToken).ConfigureAwait(false);
        Assert.AreEqual("41", stillStopped.GetProperty("result").GetString());
        await DisconnectAsync(client).ConfigureAwait(false);
    }

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
    /// Reports array sizes after repeated physical variable and expression assignments in one client session.
    /// </summary>
    /// <param name="paging">Whether the client advertises variable paging.</param>
    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    [Timeout(30000, CooperativeCancellation = true)]
    public async Task ArrayAssignmentsReportReplacementCounts(bool paging)
    {
        DapTestClient client = await DapTestClient.CreateAsync(TestContext.CancellationToken).ConfigureAwait(false);
        await using ConfiguredAsyncDisposable cleanup = client.ConfigureAwait(false);
        int frameId = await StopAtInitializedArraysAsync(client, paging).ConfigureAwait(false);
        foreach (string command in new[] { "setVariable", "setExpression" })
        {
            foreach ((string expression, int? length) in new (string, int?)[]
            {
                ("vector", 3),
                ("empty", 0),
                ("absent", null)
            })
            {
                await AssertArrayAssignmentAsync(client, frameId, command, paging, expression, length)
                    .ConfigureAwait(false);
            }
        }

        await DisconnectAsync(client).ConfigureAwait(false);
    }

    private async Task AssertArrayAssignmentAsync(
        DapTestClient client,
        int frameId,
        string command,
        bool paging,
        string expression,
        int? length)
    {
        TestContext.WriteLine($"Assigning {expression} through {command} with paging {paging}.");
        int locals = await ReadLocalsReferenceAsync(client, frameId).ConfigureAwait(false);
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

    /// <summary>
    /// Releases rejected array pages while preserving published child identities and later inspection.
    /// </summary>
    [TestMethod]
    [Timeout(30000, CooperativeCancellation = true)]
    public async Task RejectedArrayPagesReleaseUnpublishedValues()
    {
        DapTestClient client = await DapTestClient.CreateAsync(TestContext.CancellationToken).ConfigureAwait(false);
        await using ConfiguredAsyncDisposable cleanup = client.ConfigureAwait(false);
        using DapTestCancellationCapture protocolCapture = CaptureProtocolOnCancellation(client);
        try
        {
            int frameId = await StopAtInitializedArraysAsync(client).ConfigureAwait(false);
            JsonElement array = await ReadEvaluationAsync(client, frameId, "many", success: true,
                TestContext.CancellationToken).ConfigureAwait(false);
            int reference = array.GetProperty("variablesReference").GetInt32();
            JsonElement published = Assert.ContainsSingle(await ReadPageAsync(client, reference, 0, 1).ConfigureAwait(false));
            int childReference = published.GetProperty("variablesReference").GetInt32();
            Assert.IsGreaterThan(0, childReference);
            Assert.AreEqual("[0]", published.GetProperty("name").GetString());
            JsonElement[] initial = await ReadPageAsync(client, childReference, 0, 1).ConfigureAwait(false);
            Assert.AreEqual("41", Assert.ContainsSingle(initial).GetProperty("value").GetString());

            for (int attempt = 0; attempt < 2; attempt++)
            {
                JsonElement rejected = await RequestPageAsync(client, reference, 0, 65536, success: false)
                    .ConfigureAwait(false);
                Assert.Contains("retained-value limit of 65536",
                    Assert.IsInstanceOfType<string>(rejected.GetProperty("message").GetString()));
                JsonElement last = Assert.ContainsSingle(await ReadPageAsync(client, reference, 65536, 1)
                    .ConfigureAwait(false));
                Assert.AreEqual("[65536]", last.GetProperty("name").GetString());
                Assert.IsGreaterThan(0, last.GetProperty("variablesReference").GetInt32());
                JsonElement tailValue = Assert.ContainsSingle(await ReadPageAsync(client,
                    last.GetProperty("variablesReference").GetInt32(), 0, 1).ConfigureAwait(false));
                Assert.AreEqual("41", tailValue.GetProperty("value").GetString());
                JsonElement[] values = await ReadPageAsync(client, childReference, 0, 1).ConfigureAwait(false);
                Assert.AreSequenceEqual(initial.Select(value => value.GetRawText()), values.Select(value => value.GetRawText()));
            }

            await DisconnectAsync(client).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (TestContext.CancellationToken.IsCancellationRequested)
        {
            await DebuggerProcessDiagnostics.CaptureAsync(client.HostProcessId, TestContext).ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// Reuses stopped-state array elements until a direct assignment replaces their storage.
    /// </summary>
    [TestMethod]
    [Timeout(30000, CooperativeCancellation = true)]
    public async Task RepeatedArrayPagesReuseRetainedElements()
    {
        DapTestClient client = await DapTestClient.CreateAsync(TestContext.CancellationToken).ConfigureAwait(false);
        await using ConfiguredAsyncDisposable cleanup = client.ConfigureAwait(false);
        int frameId = await StopAtInitializedArraysAsync(client).ConfigureAwait(false);
        JsonElement array = await ReadEvaluationAsync(client, frameId, "many", success: true,
            TestContext.CancellationToken).ConfigureAwait(false);
        int reference = array.GetProperty("variablesReference").GetInt32();
        JsonElement[] first = await ReadPageAsync(client, reference, 0, 3).ConfigureAwait(false);
        Assert.HasCount(3, first);
        int[] expected = [.. first.Select(value => value.GetProperty("variablesReference").GetInt32())];
        Assert.IsTrue(expected.All(id => id > 0));

        for (int attempt = 0; attempt < 3; attempt++)
        {
            JsonElement[] refreshed = await ReadPageAsync(client, reference, 0, 3).ConfigureAwait(false);
            Assert.AreSequenceEqual(expected,
                refreshed.Select(value => value.GetProperty("variablesReference").GetInt32()));
        }

        int locals = await ReadLocalsReferenceAsync(client, frameId).ConfigureAwait(false);
        int assignment = await client.SendRequestAsync("setVariable", writer =>
        {
            writer.WriteStartObject();
            writer.WriteNumber("variablesReference", locals);
            writer.WriteString("name", "many");
            writer.WriteString("value", "jagged");
            writer.WriteEndObject();
        }, TestContext.CancellationToken).ConfigureAwait(false);
        using (JsonDocument response = await client.ReadMessageAsync(TestContext.CancellationToken).ConfigureAwait(false))
        {
            AssertResponse(response.RootElement, assignment, "setVariable", success: true);
        }

        using (JsonDocument invalidated = await client.ReadMessageAsync(TestContext.CancellationToken).ConfigureAwait(false))
        {
            AssertEvent(invalidated.RootElement, "invalidated");
        }

        JsonElement replacement = await ReadEvaluationAsync(client, frameId, "many", success: true,
            TestContext.CancellationToken).ConfigureAwait(false);
        int replacementReference = replacement.GetProperty("variablesReference").GetInt32();
        Assert.AreNotEqual(reference, replacementReference);
        JsonElement[] replacementElements = await ReadPageAsync(client, replacementReference, 0, 0)
            .ConfigureAwait(false);
        Assert.HasCount(4, replacementElements);

        await DisconnectAsync(client).ConfigureAwait(false);
    }

}
