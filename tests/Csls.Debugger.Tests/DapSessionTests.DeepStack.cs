using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

namespace Csls.Debugger.Tests;

/// <summary>
/// Verifies bounded inspection of independently measured recursive target stacks.
/// </summary>
public sealed partial class DapSessionTests
{
    private static readonly string[] s_deepStackStartupEvents = ["process", "breakpoint", "module", "thread"];

    /// <summary>
    /// Pages beyond the former stack-depth limit while preserving exact activation identities.
    /// </summary>
    [TestMethod]
    [Timeout(30000, CooperativeCancellation = true)]
    public Task DeepStackPagesPreserveDepthAndIdentity() => ExerciseDeepStackPagesAsync(5000);

    /// <summary>
    /// Inspects a real one-hundred-thousand-frame stack within an explicit target stack budget.
    /// </summary>
    [TestMethod]
    [TestCategory("DebuggerStress")]
    [Timeout(60000, CooperativeCancellation = true)]
    public Task DeepStackStressPagesPreserveDepthAndIdentity() => ExerciseDeepStackPagesAsync(100000);

    /// <summary>
    /// Rejects oversized and unbounded responses while preserving usable frames and exact tail pages.
    /// </summary>
    [TestMethod]
    [Timeout(30000, CooperativeCancellation = true)]
    public async Task DeepStackPageLimitsPreserveStoppedSession()
    {
        const int depth = 5000;
        DapTestClient client = await DapTestClient.CreateAsync(TestContext.CancellationToken).ConfigureAwait(false);
        await using ConfiguredAsyncDisposable disposal = client.ConfigureAwait(false);
        (int threadId, string sourcePath) = await StartDeepStackAsync(client, depth).ConfigureAwait(false);
        JsonElement top = await ReadDeepStackPageAsync(client, threadId, 0, 1).ConfigureAwait(false);
        JsonElement maximum = await ReadDeepStackPageAsync(client, threadId, 0, 4096).ConfigureAwait(false);
        Assert.AreEqual(4096, maximum.GetProperty("stackFrames").GetArrayLength());
        await AssertDeepStackArgumentAsync(client, maximum.GetProperty("stackFrames")[4095], "remaining", 4095)
            .ConfigureAwait(false);
        foreach (int levels in new[] { 4097, int.MaxValue, 0 })
        {
            int sequence = await SendDeepStackRequestAsync(client, threadId, 0, levels).ConfigureAwait(false);
            using JsonDocument response = await client.ReadMessageAsync(TestContext.CancellationToken).ConfigureAwait(false);
            AssertResponse(response.RootElement, sequence, "stackTrace", success: false);
            Assert.Contains("4096", response.RootElement.GetProperty("message").GetString()!);
        }

        foreach ((int start, int levels) in new[] { (-1, 1), (0, -1) })
        {
            int sequence = await SendDeepStackRequestAsync(client, threadId, start, levels).ConfigureAwait(false);
            using JsonDocument response = await client.ReadMessageAsync(TestContext.CancellationToken).ConfigureAwait(false);
            AssertResponse(response.RootElement, sequence, "stackTrace", success: false);
            Assert.Contains(start < 0 ? "startFrame" : "levels", response.RootElement.GetProperty("message").GetString()!);
        }

        JsonElement tail = await ReadDeepStackPageAsync(client, threadId, depth - 2, 0).ConfigureAwait(false);
        Assert.IsGreaterThanOrEqualTo(2, tail.GetProperty("stackFrames").GetArrayLength());
        Assert.AreEqual(depth - 2 + tail.GetProperty("stackFrames").GetArrayLength(),
            tail.GetProperty("totalFrames").GetInt32());
        JsonElement beyond = await ReadDeepStackPageAsync(client, threadId, int.MaxValue, 1).ConfigureAwait(false);
        Assert.AreEqual(0, beyond.GetProperty("stackFrames").GetArrayLength());
        Assert.AreEqual(tail.GetProperty("totalFrames").GetInt32(), beyond.GetProperty("totalFrames").GetInt32());
        JsonElement unchanged = await ReadDeepStackPageAsync(client, threadId, 0, 1).ConfigureAwait(false);
        AssertSameLogicalFrame(top.GetProperty("stackFrames")[0], unchanged.GetProperty("stackFrames")[0]);
        await AssertDeepStackArgumentAsync(client, unchanged.GetProperty("stackFrames")[0], "entered", depth)
            .ConfigureAwait(false);
        await FinishDeepStackAsync(client, sourcePath).ConfigureAwait(false);
    }

    /// <summary>
    /// Reacquires a deep activation after target execution and assigns its exact original argument slot.
    /// </summary>
    [TestMethod]
    [Timeout(30000, CooperativeCancellation = true)]
    public async Task DeepStackFrameSurvivesEvaluation()
    {
        const int depth = 5000;
        DapTestClient client = await DapTestClient.CreateAsync(TestContext.CancellationToken).ConfigureAwait(false);
        await using ConfiguredAsyncDisposable disposal = client.ConfigureAwait(false);
        (int threadId, string sourcePath) = await StartDeepStackAsync(client, depth).ConfigureAwait(false);
        JsonElement top = (await ReadDeepStackPageAsync(client, threadId, 0, 1).ConfigureAwait(false))
            .GetProperty("stackFrames")[0];
        JsonElement deep = (await ReadDeepStackPageAsync(client, threadId, depth - 1, 1).ConfigureAwait(false))
            .GetProperty("stackFrames")[0];
        JsonElement executed = await ReadEvaluationAsync(client, top.GetProperty("id").GetInt32(),
            "Csls.TestProcessHost.DebuggerDeepStackFixture.AddOne(41)", success: true, TestContext.CancellationToken)
            .ConfigureAwait(false);
        Assert.AreEqual("42", executed.GetProperty("result").GetString());

        int scopesSequence = await client.SendRequestAsync("scopes",
            writer => WriteFrameArguments(writer, deep.GetProperty("id").GetInt32()), TestContext.CancellationToken)
            .ConfigureAwait(false);
        using (JsonDocument scopes = await client.ReadMessageAsync(TestContext.CancellationToken).ConfigureAwait(false))
        {
            AssertResponse(scopes.RootElement, scopesSequence, "scopes", success: true);
            Assert.AreEqual(2, scopes.RootElement.GetProperty("body").GetProperty("scopes").GetArrayLength());
        }

        await AssertDeepStackArgumentAsync(client, deep, "remaining", depth - 1).ConfigureAwait(false);
        int assignmentSequence = await client.SendRequestAsync("setExpression", writer =>
        {
            writer.WriteStartObject();
            writer.WriteNumber("frameId", deep.GetProperty("id").GetInt32());
            writer.WriteString("expression", "entered");
            writer.WriteString("value", "Csls.TestProcessHost.DebuggerDeepStackFixture.AddOne(1)");
            writer.WriteEndObject();
        }, TestContext.CancellationToken).ConfigureAwait(false);
        using (JsonDocument assignment = await client.ReadMessageAsync(TestContext.CancellationToken).ConfigureAwait(false))
        {
            AssertResponse(assignment.RootElement, assignmentSequence, "setExpression", success: true);
            Assert.AreEqual("2", assignment.RootElement.GetProperty("body").GetProperty("value").GetString());
        }

        await AssertDeepStackArgumentAsync(client, deep, "entered", 2).ConfigureAwait(false);
        await AssertDeepStackArgumentAsync(client, top, "entered", depth).ConfigureAwait(false);
        JsonElement refreshed = (await ReadDeepStackPageAsync(client, threadId, depth - 1, 1).ConfigureAwait(false))
            .GetProperty("stackFrames")[0];
        AssertSameLogicalFrame(deep, refreshed);
        await FinishDeepStackAsync(client, sourcePath).ConfigureAwait(false);
    }

    private async Task ExerciseDeepStackPagesAsync(int depth)
    {
        DapTestClient client = await DapTestClient.CreateAsync(TestContext.CancellationToken)
            .ConfigureAwait(false);
        await using ConfiguredAsyncDisposable disposal = client.ConfigureAwait(false);
        (int threadId, string sourcePath) = await StartDeepStackAsync(client, depth).ConfigureAwait(false);
        JsonElement top = await ReadDeepStackPageAsync(client, threadId, 0, 2).ConfigureAwait(false);
        JsonElement[] topFrames = [.. top.GetProperty("stackFrames").EnumerateArray()];
        Assert.HasCount(2, topFrames);
        Assert.IsFalse(top.TryGetProperty("totalFrames", out _), "A partial walk must not invent a total.");
        await AssertDeepStackArgumentAsync(client, topFrames[0], "remaining", 0).ConfigureAwait(false);
        await AssertDeepStackArgumentAsync(client, topFrames[0], "entered", depth).ConfigureAwait(false);

        JsonElement deep = await ReadDeepStackPageAsync(client, threadId, depth - 2, 2)
            .ConfigureAwait(false);
        JsonElement[] deepFrames = [.. deep.GetProperty("stackFrames").EnumerateArray()];
        Assert.HasCount(2, deepFrames);
        await AssertDeepStackArgumentAsync(client, deepFrames[0], "remaining", depth - 2).ConfigureAwait(false);
        await AssertDeepStackArgumentAsync(client, deepFrames[1], "entered", 1).ConfigureAwait(false);
        JsonElement overlap = await ReadDeepStackPageAsync(client, threadId, depth - 1, 1)
            .ConfigureAwait(false);
        Assert.AreEqual(deepFrames[1].GetProperty("id").GetInt32(),
            overlap.GetProperty("stackFrames")[0].GetProperty("id").GetInt32());

        JsonElement tail = await ReadDeepStackPageAsync(client, threadId, depth, 64).ConfigureAwait(false);
        int tailCount = tail.GetProperty("stackFrames").GetArrayLength();
        Assert.IsLessThan(64, tailCount);
        Assert.AreEqual(depth + tailCount, tail.GetProperty("totalFrames").GetInt32());
        JsonElement empty = await ReadDeepStackPageAsync(client, threadId, depth + tailCount, 1)
            .ConfigureAwait(false);
        Assert.AreEqual(0, empty.GetProperty("stackFrames").GetArrayLength());
        Assert.AreEqual(depth + tailCount, empty.GetProperty("totalFrames").GetInt32());

        TestContext.WriteLine($"Inspected target depth {depth}, stack budget 33554432 bytes, top/deep page sizes 2, exact total {depth + tailCount}.");
        await FinishDeepStackAsync(client, sourcePath).ConfigureAwait(false);
    }

    private async Task FinishDeepStackAsync(DapTestClient client, string sourcePath)
    {
        await ClearSourceBreakpointsAsync(client, sourcePath).ConfigureAwait(false);
        int sequence = await client.SendRequestAsync("continue", WriteEmptyObject, TestContext.CancellationToken)
            .ConfigureAwait(false);
        await ReadSuccessfulTerminationAsync(client, sequence, TestContext.CancellationToken).ConfigureAwait(false);
        Assert.AreEqual(0, await client.WaitForExitAsync(TestContext.CancellationToken).ConfigureAwait(false));
        Assert.AreEqual(string.Empty, client.Diagnostics.ToString());
    }

    private async Task<(int ThreadId, string SourcePath)> StartDeepStackAsync(DapTestClient client, int depth)
    {
        string sourcePath = Path.Join(FindRepositoryRoot(), "tests", "Csls.TestProcessHost", "DebuggerDeepStackFixture.cs");
        int line = FindSourceLine(await File.ReadAllLinesAsync(sourcePath, TestContext.CancellationToken)
            .ConfigureAwait(false), "return CompleteDescent(entered);");
        int initializeSequence = await client.SendRequestAsync("initialize", WriteEmptyObject, TestContext.CancellationToken)
            .ConfigureAwait(false);
        using (JsonDocument initialize = await client.ReadMessageAsync(TestContext.CancellationToken).ConfigureAwait(false))
        {
            AssertResponse(initialize.RootElement, initializeSequence, "initialize", success: true);
        }

        int launchSequence = await client.SendRequestAsync("launch", writer => WriteLaunchArguments(
            writer, ResolveTestProcessHost(), ["--debugger-deep-stack-fixture", depth.ToString(CultureInfo.InvariantCulture)],
            wait: true, noDebug: false), TestContext.CancellationToken).ConfigureAwait(false);
        using (JsonDocument initialized = await client.ReadMessageAsync(TestContext.CancellationToken).ConfigureAwait(false))
        {
            AssertEvent(initialized.RootElement, "initialized");
        }

        int breakpointSequence = await client.SendRequestAsync("setBreakpoints",
            writer => WriteSourceBreakpointArguments(writer, sourcePath, line), TestContext.CancellationToken)
            .ConfigureAwait(false);
        using (JsonDocument breakpoint = await client.ReadMessageAsync(TestContext.CancellationToken).ConfigureAwait(false))
        {
            AssertResponse(breakpoint.RootElement, breakpointSequence, "setBreakpoints", success: true);
        }

        int configurationSequence = await client.SendRequestAsync("configurationDone", WriteEmptyObject,
            TestContext.CancellationToken).ConfigureAwait(false);
        bool configured = false;
        bool launched = false;
        int threadId = 0;
        StringBuilder output = new();
        string expectedOutput = $"depth:{depth.ToString(CultureInfo.InvariantCulture)}";
        while (!configured || !launched || threadId == 0 || output.Length < expectedOutput.Length)
        {
            using JsonDocument message = await client.ReadMessageAsync(TestContext.CancellationToken).ConfigureAwait(false);
            JsonElement root = message.RootElement;
            if (root.TryGetProperty("request_seq", out JsonElement requestSequence))
            {
                int sequence = requestSequence.GetInt32();
                Assert.Contains(sequence, new[] { launchSequence, configurationSequence });
                AssertResponse(root, sequence, sequence == launchSequence ? "launch" : "configurationDone", success: true);
                configured |= sequence == configurationSequence;
                launched |= sequence == launchSequence;
                continue;
            }

            string? eventName = root.GetProperty("event").GetString();
            if (eventName == "output")
            {
                Assert.AreEqual("stdout", root.GetProperty("body").GetProperty("category").GetString());
                output.Append(root.GetProperty("body").GetProperty("output").GetString());
            }
            else if (eventName == "stopped")
            {
                Assert.AreEqual("breakpoint", root.GetProperty("body").GetProperty("reason").GetString());
                Assert.AreEqual(0, threadId, "The fixture must stop only once after completing its descent.");
                threadId = root.GetProperty("body").GetProperty("threadId").GetInt32();
            }
            else
            {
                Assert.Contains(eventName, s_deepStackStartupEvents);
            }
        }

        Assert.AreEqual(expectedOutput, output.ToString(), "Target-side descent must independently prove the depth.");
        return (threadId, sourcePath);
    }

    private async Task<JsonElement> ReadDeepStackPageAsync(DapTestClient client, int threadId, int start, int levels)
    {
        int sequence = await SendDeepStackRequestAsync(client, threadId, start, levels).ConfigureAwait(false);
        using JsonDocument response = await client.ReadMessageAsync(TestContext.CancellationToken).ConfigureAwait(false);
        AssertResponse(response.RootElement, sequence, "stackTrace", success: true);
        return response.RootElement.GetProperty("body").Clone();
    }

    private Task<int> SendDeepStackRequestAsync(DapTestClient client, int threadId, int start, int levels) =>
        client.SendRequestAsync("stackTrace", writer =>
        {
            writer.WriteStartObject();
            writer.WriteNumber("threadId", threadId);
            writer.WriteNumber("startFrame", start);
            writer.WriteNumber("levels", levels);
            writer.WriteEndObject();
        }, TestContext.CancellationToken);

    private async Task AssertDeepStackArgumentAsync(DapTestClient client, JsonElement frame, string name, int expected)
    {
        Assert.AreEqual("Csls.TestProcessHost.DebuggerDeepStackFixture.Descend", frame.GetProperty("name").GetString());
        JsonElement value = await ReadEvaluationAsync(client, frame.GetProperty("id").GetInt32(), name,
            success: true, TestContext.CancellationToken).ConfigureAwait(false);
        Assert.AreEqual(expected.ToString(CultureInfo.InvariantCulture), value.GetProperty("result").GetString());
        Assert.AreEqual("int", value.GetProperty("type").GetString());
    }
}
