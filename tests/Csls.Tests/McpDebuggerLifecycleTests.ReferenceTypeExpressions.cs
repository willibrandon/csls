using ModelContextProtocol.Client;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Csls.Tests;

/// <summary>
/// Exercises non-executing reference casts through real MCP and debugger worker transports.
/// </summary>
public sealed partial class McpDebuggerLifecycleTests
{
    /// <summary>
    /// Preserves observation-only authorization, watch isolation, and current stopped generations during casts.
    /// </summary>
    [TestMethod]
    [Timeout(60000, CooperativeCancellation = true)]
    [OSCondition(ConditionMode.Exclude, OperatingSystems.Windows)]
    public async Task McpReferenceTypeExpressionsRemainReadOnly()
    {
        string repositoryRoot = EditorToolResolver.FindRepositoryRoot();
        string sourcePath = Path.Join(repositoryRoot, "tests", "Csls.TestProcessHost", "ReferenceAssignmentFixture.cs");
        int line = (await File.ReadAllLinesAsync(sourcePath, TestContext.CancellationToken).ConfigureAwait(false))
            .Select(static (text, index) => (Text: text, Line: index + 1))
            .Single(static candidate => candidate.Text.Contains("int result = DebuggerFixture.WaitForSignal(",
                StringComparison.Ordinal)).Line;
        DirectoryInfo directory = Directory.CreateTempSubdirectory("csls-mcp-reference-types-");
        try
        {
            await ExerciseMcpReferenceTypeExpressionsAsync(repositoryRoot, sourcePath, line, directory.FullName,
                TestContext.CancellationToken).ConfigureAwait(false);
        }
        finally
        {
            await DirectoryReleaseWaiter.DeleteAsync(directory.FullName, TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        }
    }

    private static async Task ExerciseMcpReferenceTypeExpressionsAsync(
        string repositoryRoot, string sourcePath, int line, string directory, CancellationToken cancellationToken)
    {
        const string BaseField = "((Csls.TestProcessHost.ReferenceCastBase)hiddenObject)._value";
        McpProcessSession mcp = await StartMcpAsync(cancellationToken).ConfigureAwait(false);
        await using ConfiguredAsyncDisposable cleanup = mcp.ConfigureAwait(false);
        McpClient client = mcp.Client;
        JsonElement started = await StartTargetAsync(client, repositoryRoot, sourcePath, line,
            Path.Join(directory, "continue.signal"), cancellationToken, "--debugger-reference-assignment-fixture")
            .ConfigureAwait(false);
        string session = started.GetProperty("debugSession").GetString()!;
        ProcessExitObservation exit = ProcessExitWaiter.Observe(started.GetProperty("processId").GetInt32());
        JsonElement stopped = await WaitForStoppedAsync(client, session, cancellationToken).ConfigureAwait(false);
        Assert.AreEqual("breakpoint", stopped.GetProperty("stopReason").GetString());
        Assert.IsFalse(stopped.GetProperty("agentControl").GetBoolean());
        long generation = stopped.GetProperty("stopGeneration").GetInt64();
        int threadId = stopped.GetProperty("stoppedThreadId").GetInt32();
        JsonElement frame = await GetSourceFrameAsync(client, session, generation, threadId, sourcePath, cancellationToken)
            .ConfigureAwait(false);
        int frameId = frame.GetProperty("id").GetInt32();
        await AssertMcpStructAssignmentIntegerAsync(client, session, generation, frameId, BaseField, "11", cancellationToken)
            .ConfigureAwait(false);
        await AssertMcpReferenceMessageAsync(client, session, generation, frameId,
            "((System.ArgumentException)widenedSource)", "widened source", cancellationToken).ConfigureAwait(false);
        JsonElement typeTest = await EvaluateMcpStructAssignmentAsync(client, session, generation, frameId,
            "widenedSource is System.ArgumentException", cancellationToken).ConfigureAwait(false);
        Assert.AreEqual("true", typeTest.GetProperty("result").GetString());
        Assert.AreEqual("bool", typeTest.GetProperty("type").GetString());

        await AssertMcpReferenceCastWatchesAsync(client, session, generation, frameId, cancellationToken).ConfigureAwait(false);
        await AssertToolErrorAsync(client, "debug_expression_set", new Dictionary<string, object?>
        {
            ["debugSession"] = session,
            ["stopGeneration"] = generation,
            ["frameId"] = frameId,
            ["expression"] = BaseField,
            ["value"] = "99"
        }, "debugger_control_denied", cancellationToken).ConfigureAwait(false);
        await AssertToolErrorAsync(client, "debug_evaluate", new Dictionary<string, object?>
        {
            ["debugSession"] = session,
            ["stopGeneration"] = checked(generation + 1),
            ["frameId"] = frameId,
            ["expression"] = BaseField
        }, "debugger_stale_generation", cancellationToken).ConfigureAwait(false);
        await AssertMcpStructAssignmentIntegerAsync(client, session, generation, frameId, BaseField, "11", cancellationToken)
            .ConfigureAwait(false);
        await AssertMcpStructAssignmentIntegerAsync(client, session, generation, frameId, "factory._calls", "0", cancellationToken)
            .ConfigureAwait(false);
        JsonElement state = await CallAsync(client, "debug_session_get",
            new Dictionary<string, object?> { ["debugSession"] = session }, cancellationToken).ConfigureAwait(false);
        Assert.AreEqual("stopped", state.GetProperty("state").GetString());
        Assert.AreEqual(generation, state.GetProperty("stopGeneration").GetInt64());
        Assert.IsFalse(state.GetProperty("agentControl").GetBoolean());
        JsonElement refreshedFrame = await GetSourceFrameAsync(client, session, generation, threadId, sourcePath, cancellationToken)
            .ConfigureAwait(false);
        Assert.AreEqual(frameId, refreshedFrame.GetProperty("id").GetInt32());
        string diagnostics = await mcp.DisconnectAsync(TimeSpan.FromSeconds(20), cancellationToken).ConfigureAwait(false);
        Assert.DoesNotContain("fail:", diagnostics, StringComparison.OrdinalIgnoreCase);
        await ProcessExitWaiter.WaitAsync(exit, TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(false);
    }

    private static async Task AssertMcpReferenceCastWatchesAsync(
        McpClient client, string session, long generation, int frameId, CancellationToken cancellationToken)
    {
        string[] expressions =
        [
            "widenedSource is System.ArgumentException",
            "(System.InvalidOperationException)widenedSource",
            "widenedSource as System.InvalidOperationException"
        ];
        JsonElement watches = await CallAsync(client, "debug_watches_get", new Dictionary<string, object?>
        {
            ["debugSession"] = session,
            ["stopGeneration"] = generation,
            ["frameId"] = frameId,
            ["expressions"] = expressions
        }, cancellationToken).ConfigureAwait(false);
        Assert.AreEqual(generation, watches.GetProperty("stopGeneration").GetInt64());
        JsonElement[] values = [.. watches.GetProperty("watches").EnumerateArray()];
        Assert.HasCount(3, values);
        Assert.AreEqual("true", values[0].GetProperty("evaluation").GetProperty("result").GetString());
        Assert.AreEqual("bool", values[0].GetProperty("evaluation").GetProperty("type").GetString());
        Assert.AreEqual("debugger_evaluation_failed", values[1].GetProperty("error").GetProperty("code").GetString());
        Assert.IsFalse(values[1].TryGetProperty("evaluation", out _));
        Assert.AreEqual("null", values[2].GetProperty("evaluation").GetProperty("result").GetString());
        Assert.AreEqual("System.InvalidOperationException", values[2].GetProperty("evaluation").GetProperty("type").GetString());
        Assert.AreEqual(0, values[2].GetProperty("evaluation").GetProperty("variablesReference").GetInt32());

        string uri = $"csls://debug/watches/{session}/{generation}/{frameId}?expression={Uri.EscapeDataString(expressions[0])}";
        JsonElement resource = await ReadAsync(client, uri, cancellationToken).ConfigureAwait(false);
        Assert.AreEqual(generation, resource.GetProperty("stopGeneration").GetInt64());
        JsonElement resourceWatch = Assert.ContainsSingle(resource.GetProperty("watches").EnumerateArray());
        Assert.AreEqual("true", resourceWatch.GetProperty("evaluation").GetProperty("result").GetString());
        Assert.AreEqual("bool", resourceWatch.GetProperty("evaluation").GetProperty("type").GetString());
    }
}
