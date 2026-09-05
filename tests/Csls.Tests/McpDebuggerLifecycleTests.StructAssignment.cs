using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Csls.Tests;

/// <summary>
/// Verifies whole-value assignment and destination snapshot ownership through real MCP processes.
/// </summary>
public sealed partial class McpDebuggerLifecycleTests
{
    /// <summary>
    /// Copies a struct without execution and reuses its destination snapshot through read-only MCP inspection.
    /// </summary>
    /// <param name="setVariable">Whether to assign through the locals container instead of an expression.</param>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    [Timeout(60000, CooperativeCancellation = true)]
    [OSCondition(ConditionMode.Exclude, OperatingSystems.Windows)]
    public async Task McpStructAssignmentPreservesDestinationSnapshot(bool setVariable)
    {
        string repositoryRoot = EditorToolResolver.FindRepositoryRoot();
        string sourcePath = Path.Join(
            repositoryRoot, "tests", "Csls.TestProcessHost", "DebuggerFixture.cs");
        int breakpointLine = (await File.ReadAllLinesAsync(
            sourcePath, TestContext.CancellationToken).ConfigureAwait(false))
            .Select(static (line, index) => (Text: line, Number: index + 1))
            .Single(static line => line.Text.Contains(
                "Console.Write(announcement);", StringComparison.Ordinal)).Number;
        string testDirectory = Path.Join(
            Path.GetTempPath(), $"csls-mcp-struct-assignment-{Guid.NewGuid():N}");
        Directory.CreateDirectory(testDirectory);
        try
        {
            await ExerciseMcpStructAssignmentAsync(
                repositoryRoot, sourcePath, breakpointLine, testDirectory,
                setVariable, TestContext.CancellationToken).ConfigureAwait(false);
        }
        finally
        {
            await DirectoryReleaseWaiter.DeleteAsync(
                testDirectory, TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        }
    }

    private static async Task ExerciseMcpStructAssignmentAsync(
        string repositoryRoot,
        string sourcePath,
        int breakpointLine,
        string testDirectory,
        bool setVariable,
        CancellationToken cancellationToken)
    {
        const string Destination = "localResultsViewStruct";
        const string Source = "localStructSource";
        McpProcessSession mcp = await StartMcpAsync(cancellationToken).ConfigureAwait(false);
        await using ConfiguredAsyncDisposable cleanup = mcp.ConfigureAwait(false);
        McpClient client = mcp.Client;
        JsonElement started = await StartTargetAsync(
            client, repositoryRoot, sourcePath, breakpointLine,
            Path.Join(testDirectory, "continue.signal"), cancellationToken).ConfigureAwait(false);
        string debugSession = started.GetProperty("debugSession").GetString()!;
        ProcessExitObservation exit = ProcessExitWaiter.Observe(started.GetProperty("processId").GetInt32());
        JsonElement stopped = await WaitForStoppedAsync(
            client, debugSession, cancellationToken).ConfigureAwait(false);
        Assert.AreEqual("breakpoint", stopped.GetProperty("stopReason").GetString());
        Assert.IsFalse(stopped.GetProperty("agentControl").GetBoolean());
        long generation = stopped.GetProperty("stopGeneration").GetInt64();
        Assert.IsGreaterThan(0, generation);
        JsonElement frame = await GetSourceFrameAsync(
            client, debugSession, generation, stopped.GetProperty("stoppedThreadId").GetInt32(),
            sourcePath, cancellationToken).ConfigureAwait(false);
        int frameId = frame.GetProperty("id").GetInt32();
        int localsReference = await GetMcpStructAssignmentLocalsAsync(
            client, debugSession, generation, frameId, cancellationToken).ConfigureAwait(false);
        string tool = setVariable ? "debug_variable_set" : "debug_expression_set";
        var arguments = new Dictionary<string, object?>
        {
            ["debugSession"] = debugSession,
            ["stopGeneration"] = generation,
            ["value"] = Source
        };
        if (setVariable)
        {
            arguments["variablesReference"] = localsReference;
            arguments["name"] = Destination;
        }
        else
        {
            arguments["frameId"] = frameId;
            arguments["expression"] = Destination;
        }

        await AssertToolErrorAsync(client, tool, arguments,
            "debugger_control_denied", cancellationToken).ConfigureAwait(false);
        JsonElement granted = await GrantAgentControlAsync(
            client, debugSession, durationSeconds: 60, cancellationToken).ConfigureAwait(false);
        Assert.AreEqual(generation, granted.GetProperty("stopGeneration").GetInt64());
        JsonElement seeded = await CallAsync(client, "debug_expression_set",
            new Dictionary<string, object?>
            {
                ["debugSession"] = debugSession,
                ["stopGeneration"] = generation,
                ["frameId"] = frameId,
                ["expression"] = Destination + "._state._items[0]",
                ["value"] = "141"
            }, cancellationToken).ConfigureAwait(false);
        Assert.AreEqual(generation, seeded.GetProperty("stopGeneration").GetInt64());
        Assert.IsFalse(seeded.GetProperty("targetCodeExecuted").GetBoolean());
        Assert.AreEqual("141", seeded.GetProperty("variable").GetProperty("value").GetString());
        await AssertMcpStructAssignmentIntegerAsync(
            client, debugSession, generation, frameId, Destination + "._state._items[0]",
            "141", cancellationToken).ConfigureAwait(false);
        await AssertMcpStructAssignmentIntegerAsync(
            client, debugSession, generation, frameId, Source + "._state._items[0]",
            "151", cancellationToken).ConfigureAwait(false);

        JsonElement assigned = await AssertResourceSubscriptionAsync(
            client, $"csls://debug/variables/{debugSession}/{generation}/{localsReference}",
            () => CallAsync(client, tool, arguments, cancellationToken), cancellationToken)
            .ConfigureAwait(false);
        Assert.AreEqual(debugSession, assigned.GetProperty("debugSession").GetString());
        Assert.AreEqual(generation, assigned.GetProperty("stopGeneration").GetInt64());
        Assert.IsFalse(assigned.GetProperty("targetCodeExecuted").GetBoolean());
        JsonElement variable = assigned.GetProperty("variable");
        Assert.AreEqual(Destination, variable.GetProperty("name").GetString());
        Assert.AreEqual(Destination, variable.GetProperty("evaluateName").GetString());
        Assert.AreEqual("Csls.TestProcessHost.ResultsViewStructFixture", variable.GetProperty("type").GetString());
        Assert.AreEqual("{Csls.TestProcessHost.ResultsViewStructFixture}", variable.GetProperty("value").GetString());
        Assert.AreEqual("normal", variable.GetProperty("presentationKind").GetString());
        int assignmentReference = variable.GetProperty("variablesReference").GetInt32();
        Assert.IsGreaterThan(0, assignmentReference);
        await AssertMcpStructAssignmentIntegerAsync(
            client, debugSession, generation, frameId, Destination + "._state._items[0]",
            "151", cancellationToken).ConfigureAwait(false);
        await AssertMcpStructAssignmentIntegerAsync(
            client, debugSession, generation, frameId, Destination + "._state._enumerationCount",
            "0", cancellationToken).ConfigureAwait(false);
        JsonElement fields = await ReadMcpStructAssignmentVariablesAsync(
            client, debugSession, generation, assignmentReference, cancellationToken).ConfigureAwait(false);
        JsonElement state = Assert.ContainsSingle(fields.GetProperty("variables").EnumerateArray()
            .Where(child => child.GetProperty("name").GetString() == "_state"));
        Assert.AreEqual(Destination + "._state", state.GetProperty("evaluateName").GetString());
        JsonElement lazy = Assert.ContainsSingle(fields.GetProperty("variables").EnumerateArray()
            .Where(child => child.GetProperty("name").GetString() == "Results View"));
        Assert.AreEqual("resultsView", lazy.GetProperty("presentationKind").GetString());
        Assert.AreEqual("Expanding the Results View will enumerate the IEnumerable", lazy.GetProperty("value").GetString());
        var lazyArguments = new Dictionary<string, object?>
        {
            ["debugSession"] = debugSession,
            ["stopGeneration"] = generation,
            ["variablesReference"] = lazy.GetProperty("variablesReference").GetInt32()
        };
        await AssertToolErrorAsync(client, "debug_variables_get", lazyArguments,
            "debugger_operation_failed", cancellationToken).ConfigureAwait(false);
        JsonElement presented = await CallAsync(
            client, "debug_variables_get_presented", lazyArguments, cancellationToken).ConfigureAwait(false);
        long snapshotGeneration = presented.GetProperty("stopGeneration").GetInt64();
        Assert.IsGreaterThan(generation, snapshotGeneration);
        JsonElement snapshot = Assert.ContainsSingle(presented.GetProperty("variables").EnumerateArray());
        int snapshotReference = AssertMcpStructAssignmentSnapshot(snapshot);
        Assert.AreNotEqual(lazy.GetProperty("variablesReference").GetInt32(), snapshotReference);
        await AssertToolErrorAsync(client, tool, arguments,
            "debugger_stale_generation", cancellationToken).ConfigureAwait(false);

        int refreshedLocalsReference = await GetMcpStructAssignmentLocalsAsync(
            client, debugSession, snapshotGeneration, frameId, cancellationToken).ConfigureAwait(false);
        Assert.AreNotEqual(localsReference, refreshedLocalsReference);
        JsonElement locals = await ReadMcpStructAssignmentVariablesAsync(
            client, debugSession, snapshotGeneration, refreshedLocalsReference, cancellationToken)
            .ConfigureAwait(false);
        JsonElement destination = Assert.ContainsSingle(locals.GetProperty("variables").EnumerateArray()
            .Where(local => local.GetProperty("name").GetString() == Destination));
        await AssertMcpStructAssignmentSnapshotReuseAsync(
            client, debugSession, snapshotGeneration, destination.GetProperty("variablesReference").GetInt32(),
            snapshotReference, cancellationToken).ConfigureAwait(false);
        arguments["stopGeneration"] = snapshotGeneration;
        arguments["value"] = "localTuple";
        if (setVariable)
        {
            arguments["variablesReference"] = refreshedLocalsReference;
        }
        await AssertMcpStructAssignmentTypeErrorAsync(client, tool, arguments, cancellationToken)
            .ConfigureAwait(false);

        JsonElement revoked = await CallAsync(client, "debug_agent_control_set",
            new Dictionary<string, object?>
            {
                ["debugSession"] = debugSession,
                ["enabled"] = false
            }, cancellationToken).ConfigureAwait(false);
        Assert.IsFalse(revoked.GetProperty("agentControl").GetBoolean());
        Assert.AreEqual(snapshotGeneration, revoked.GetProperty("stopGeneration").GetInt64());
        arguments["value"] = Source;
        await AssertToolErrorAsync(client, tool, arguments,
            "debugger_control_denied", cancellationToken).ConfigureAwait(false);
        JsonElement evaluated = await EvaluateMcpStructAssignmentAsync(
            client, debugSession, snapshotGeneration, frameId, Destination, cancellationToken)
            .ConfigureAwait(false);
        await AssertMcpStructAssignmentSnapshotReuseAsync(
            client, debugSession, snapshotGeneration, evaluated.GetProperty("variablesReference").GetInt32(),
            snapshotReference, cancellationToken).ConfigureAwait(false);
        JsonElement page = await ReadMcpStructAssignmentVariablesAsync(
            client, debugSession, snapshotGeneration, snapshotReference, cancellationToken).ConfigureAwait(false);
        AssertResultsViewSnapshotPage(page, snapshotGeneration, ["[0]", "[1]"], ["151", "152"]);
        JsonElement resource = await ReadAsync(client,
            $"csls://debug/variables/{debugSession}/{snapshotGeneration}/{snapshotReference}?start=1&count=1",
            cancellationToken).ConfigureAwait(false);
        AssertResultsViewSnapshotPage(resource, snapshotGeneration, ["[1]"], ["152"]);
        await AssertMcpStructAssignmentIntegerAsync(
            client, debugSession, snapshotGeneration, frameId, Destination + "._state._enumerationCount",
            "1", cancellationToken).ConfigureAwait(false);
        await AssertMcpStructAssignmentIntegerAsync(
            client, debugSession, snapshotGeneration, frameId, Source + "._state._enumerationCount",
            "1", cancellationToken).ConfigureAwait(false);
        JsonElement currentFrame = await GetSourceFrameAsync(
            client, debugSession, snapshotGeneration, stopped.GetProperty("stoppedThreadId").GetInt32(),
            sourcePath, cancellationToken).ConfigureAwait(false);
        Assert.AreEqual(frameId, currentFrame.GetProperty("id").GetInt32());
        string diagnostics = await mcp.DisconnectAsync(TimeSpan.FromSeconds(20), cancellationToken)
            .ConfigureAwait(false);
        Assert.DoesNotContain("fail:", diagnostics, StringComparison.OrdinalIgnoreCase);
        await ProcessExitWaiter.WaitAsync(exit, TimeSpan.FromSeconds(10), cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task<int> GetMcpStructAssignmentLocalsAsync(
        McpClient client, string debugSession, long generation, int frameId, CancellationToken cancellationToken)
    {
        JsonElement scopes = await CallAsync(client, "debug_scopes_get",
            new Dictionary<string, object?>
            {
                ["debugSession"] = debugSession,
                ["stopGeneration"] = generation,
                ["frameId"] = frameId
            }, cancellationToken).ConfigureAwait(false);
        Assert.AreEqual(generation, scopes.GetProperty("stopGeneration").GetInt64());
        JsonElement locals = Assert.ContainsSingle(scopes.GetProperty("scopes").EnumerateArray()
            .Where(scope => scope.GetProperty("name").GetString() == "Locals"));
        int reference = locals.GetProperty("variablesReference").GetInt32();
        Assert.IsGreaterThan(0, reference);
        return reference;
    }

    private static async Task<JsonElement> EvaluateMcpStructAssignmentAsync(
        McpClient client, string debugSession, long generation, int frameId,
        string expression, CancellationToken cancellationToken)
    {
        JsonElement result = await CallAsync(client, "debug_evaluate",
            new Dictionary<string, object?>
            {
                ["debugSession"] = debugSession,
                ["stopGeneration"] = generation,
                ["frameId"] = frameId,
                ["expression"] = expression
            }, cancellationToken).ConfigureAwait(false);
        Assert.AreEqual(generation, result.GetProperty("stopGeneration").GetInt64());
        JsonElement evaluation = result.GetProperty("evaluation");
        Assert.IsFalse(evaluation.GetProperty("targetCodeExecuted").GetBoolean());
        return evaluation;
    }

    private static async Task AssertMcpStructAssignmentIntegerAsync(
        McpClient client, string debugSession, long generation, int frameId,
        string expression, string expected, CancellationToken cancellationToken)
    {
        JsonElement value = await EvaluateMcpStructAssignmentAsync(
            client, debugSession, generation, frameId, expression, cancellationToken).ConfigureAwait(false);
        Assert.AreEqual(expected, value.GetProperty("result").GetString(), expression);
        Assert.AreEqual("int", value.GetProperty("type").GetString(), expression);
        Assert.AreEqual(0, value.GetProperty("variablesReference").GetInt32(), expression);
    }

    private static async Task<JsonElement> ReadMcpStructAssignmentVariablesAsync(
        McpClient client, string debugSession, long generation, int reference, CancellationToken cancellationToken)
    {
        JsonElement result = await CallAsync(client, "debug_variables_get",
            new Dictionary<string, object?>
            {
                ["debugSession"] = debugSession,
                ["stopGeneration"] = generation,
                ["variablesReference"] = reference
            }, cancellationToken).ConfigureAwait(false);
        Assert.AreEqual(generation, result.GetProperty("stopGeneration").GetInt64());
        return result;
    }

    private static int AssertMcpStructAssignmentSnapshot(JsonElement snapshot)
    {
        Assert.AreEqual("Results View", snapshot.GetProperty("name").GetString());
        Assert.AreEqual("resultsSnapshot", snapshot.GetProperty("presentationKind").GetString());
        Assert.AreEqual(2, snapshot.GetProperty("indexedVariables").GetInt32());
        Assert.AreEqual(0, snapshot.GetProperty("namedVariables").GetInt32());
        Assert.IsFalse(snapshot.TryGetProperty("evaluateName", out JsonElement expression) &&
            expression.ValueKind != JsonValueKind.Null);
        int reference = snapshot.GetProperty("variablesReference").GetInt32();
        Assert.IsGreaterThan(0, reference);
        return reference;
    }

    private static async Task AssertMcpStructAssignmentSnapshotReuseAsync(
        McpClient client, string debugSession, long generation, int parentReference,
        int snapshotReference, CancellationToken cancellationToken)
    {
        JsonElement fields = await ReadMcpStructAssignmentVariablesAsync(
            client, debugSession, generation, parentReference, cancellationToken).ConfigureAwait(false);
        JsonElement snapshot = Assert.ContainsSingle(fields.GetProperty("variables").EnumerateArray()
            .Where(child => child.GetProperty("name").GetString() == "Results View"));
        Assert.AreEqual(snapshotReference, AssertMcpStructAssignmentSnapshot(snapshot));
    }

    private static async Task AssertMcpStructAssignmentTypeErrorAsync(
        McpClient client, string tool, Dictionary<string, object?> arguments, CancellationToken cancellationToken)
    {
        CallToolResult result = await client.CallToolAsync(
            tool, arguments, cancellationToken: cancellationToken).ConfigureAwait(false);
        Assert.IsTrue(result.IsError, tool);
        Assert.IsNull(result.StructuredContent, tool);
        Assert.IsNotNull(result.Meta, tool);
        JsonNode? errorCode = result.Meta["errorCode"];
        Assert.IsNotNull(errorCode, tool);
        Assert.AreEqual("debugger_operation_failed", errorCode.GetValue<string>(), tool);
        TextContentBlock content = Assert.ContainsSingle(result.Content.OfType<TextContentBlock>());
        using var error = JsonDocument.Parse(content.Text);
        Assert.AreEqual("debugger_operation_failed", error.RootElement.GetProperty("code").GetString());
        string? message = error.RootElement.GetProperty("message").GetString();
        Assert.IsNotNull(message);
        Assert.Contains("identical loaded runtime types", message, StringComparison.Ordinal);
    }
}
