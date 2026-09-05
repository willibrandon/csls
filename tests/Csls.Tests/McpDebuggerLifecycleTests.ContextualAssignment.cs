using ModelContextProtocol.Client;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Csls.Tests;

/// <summary>
/// Verifies authorized contextual assignment through the real MCP broker and debugger worker.
/// </summary>
public sealed partial class McpDebuggerLifecycleTests
{
    /// <summary>
    /// Requires control authorization and publishes a generation-preserving change for a contextual literal.
    /// </summary>
    /// <param name="setVariable">Whether to assign through the named locals container.</param>
    /// <param name="nullable">Whether to clear a nullable with null instead of a tuple with default.</param>
    [TestMethod]
    [DataRow(false, false)]
    [DataRow(true, false)]
    [DataRow(false, true)]
    [DataRow(true, true)]
    [Timeout(60000, CooperativeCancellation = true)]
    [OSCondition(ConditionMode.Exclude, OperatingSystems.Windows)]
    public async Task McpContextualAssignmentPreservesGenerationAndRequiresControl(bool setVariable, bool nullable)
    {
        string repositoryRoot = EditorToolResolver.FindRepositoryRoot();
        string sourcePath = Path.Join(repositoryRoot, "tests", "Csls.TestProcessHost", "DebuggerFixture.cs");
        int line = (await File.ReadAllLinesAsync(sourcePath, TestContext.CancellationToken).ConfigureAwait(false))
            .Select(static (text, index) => (Text: text, Line: index + 1))
            .Single(static candidate => candidate.Text.Contains("Console.Write(announcement);", StringComparison.Ordinal)).Line;
        DirectoryInfo directory = Directory.CreateTempSubdirectory("csls-mcp-contextual-assignment-");
        try
        {
            await ExerciseMcpContextualAssignmentAsync(repositoryRoot, sourcePath, line, directory.FullName,
                setVariable, nullable, TestContext.CancellationToken).ConfigureAwait(false);
        }
        finally
        {
            await DirectoryReleaseWaiter.DeleteAsync(directory.FullName, TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        }
    }

    private static async Task ExerciseMcpContextualAssignmentAsync(
        string repositoryRoot, string sourcePath, int line, string directory, bool setVariable, bool nullable,
        CancellationToken cancellationToken)
    {
        McpProcessSession mcp = await StartMcpAsync(cancellationToken).ConfigureAwait(false);
        await using ConfiguredAsyncDisposable cleanup = mcp.ConfigureAwait(false);
        McpClient client = mcp.Client;
        JsonElement started = await StartTargetAsync(client, repositoryRoot, sourcePath, line,
            Path.Join(directory, "continue.signal"), cancellationToken).ConfigureAwait(false);
        string session = started.GetProperty("debugSession").GetString()!;
        ProcessExitObservation exit = ProcessExitWaiter.Observe(started.GetProperty("processId").GetInt32());
        JsonElement stopped = await WaitForStoppedAsync(client, session, cancellationToken).ConfigureAwait(false);
        Assert.AreEqual("breakpoint", stopped.GetProperty("stopReason").GetString());
        Assert.IsFalse(stopped.GetProperty("agentControl").GetBoolean());
        long generation = stopped.GetProperty("stopGeneration").GetInt64();
        JsonElement frame = await GetSourceFrameAsync(client, session, generation,
            stopped.GetProperty("stoppedThreadId").GetInt32(), sourcePath, cancellationToken).ConfigureAwait(false);
        int frameId = frame.GetProperty("id").GetInt32();
        int localsReference = await GetMcpStructAssignmentLocalsAsync(
            client, session, generation, frameId, cancellationToken).ConfigureAwait(false);
        string tool = setVariable ? "debug_variable_set" : "debug_expression_set";
        string destination = nullable ? "localNullable" : "localTuple";
        string expectedType = nullable ? "int?" : "(int Number, string Text)";
        string expectedValue = nullable ? "null" : "(0, null)";
        var arguments = new Dictionary<string, object?>
        {
            ["debugSession"] = session,
            ["stopGeneration"] = generation,
            ["value"] = nullable ? "null" : "default"
        };
        if (setVariable)
        {
            arguments["variablesReference"] = localsReference;
            arguments["name"] = destination;
        }
        else
        {
            arguments["frameId"] = frameId;
            arguments["expression"] = destination;
        }

        await AssertToolErrorAsync(client, tool, arguments, "debugger_control_denied", cancellationToken)
            .ConfigureAwait(false);
        JsonElement original = await EvaluateMcpStructAssignmentAsync(
            client, session, generation, frameId, destination, cancellationToken).ConfigureAwait(false);
        Assert.AreEqual(nullable ? "45" : "(42, \"answer\")", original.GetProperty("result").GetString());
        Assert.AreEqual(expectedType, original.GetProperty("type").GetString());
        JsonElement granted = await GrantAgentControlAsync(client, session, durationSeconds: 60, cancellationToken)
            .ConfigureAwait(false);
        Assert.AreEqual(generation, granted.GetProperty("stopGeneration").GetInt64());
        JsonElement assigned = await AssertResourceSubscriptionAsync(
            client, $"csls://debug/variables/{session}/{generation}/{localsReference}",
            () => CallAsync(client, tool, arguments, cancellationToken), cancellationToken).ConfigureAwait(false);
        Assert.AreEqual(session, assigned.GetProperty("debugSession").GetString());
        Assert.AreEqual(generation, assigned.GetProperty("stopGeneration").GetInt64());
        Assert.IsFalse(assigned.GetProperty("targetCodeExecuted").GetBoolean());
        JsonElement variable = assigned.GetProperty("variable");
        Assert.AreEqual(destination, variable.GetProperty("evaluateName").GetString());
        Assert.AreEqual(expectedType, variable.GetProperty("type").GetString());
        Assert.AreEqual(expectedValue, variable.GetProperty("value").GetString());
        JsonElement fields = await ReadMcpStructAssignmentVariablesAsync(client, session, generation,
            variable.GetProperty("variablesReference").GetInt32(), cancellationToken).ConfigureAwait(false);
        if (nullable)
        {
            JsonElement presence = Assert.ContainsSingle(fields.GetProperty("variables").EnumerateArray()
                .Where(field => field.GetProperty("name").GetString() == "hasValue"));
            JsonElement payload = Assert.ContainsSingle(fields.GetProperty("variables").EnumerateArray()
                .Where(field => field.GetProperty("name").GetString() == "value"));
            Assert.AreEqual("false", presence.GetProperty("value").GetString());
            Assert.AreEqual("bool", presence.GetProperty("type").GetString());
            Assert.AreEqual("0", payload.GetProperty("value").GetString());
            Assert.AreEqual("int", payload.GetProperty("type").GetString());
        }
        else
        {
            JsonElement number = Assert.ContainsSingle(fields.GetProperty("variables").EnumerateArray()
                .Where(field => field.GetProperty("name").GetString() == "Number"));
            JsonElement text = Assert.ContainsSingle(fields.GetProperty("variables").EnumerateArray()
                .Where(field => field.GetProperty("name").GetString() == "Text"));
            Assert.AreEqual("0", number.GetProperty("value").GetString());
            Assert.AreEqual("localTuple.Item1", number.GetProperty("evaluateName").GetString());
            Assert.AreEqual("null", text.GetProperty("value").GetString());
            Assert.AreEqual("localTuple.Item2", text.GetProperty("evaluateName").GetString());
        }

        JsonElement revoked = await CallAsync(client, "debug_agent_control_set",
            new Dictionary<string, object?> { ["debugSession"] = session, ["enabled"] = false }, cancellationToken)
            .ConfigureAwait(false);
        Assert.IsFalse(revoked.GetProperty("agentControl").GetBoolean());
        arguments["value"] = nullable ? "45" : "tupleArgument";
        await AssertToolErrorAsync(client, tool, arguments, "debugger_control_denied", cancellationToken)
            .ConfigureAwait(false);
        JsonElement refreshed = await EvaluateMcpStructAssignmentAsync(
            client, session, generation, frameId, destination, cancellationToken).ConfigureAwait(false);
        Assert.AreEqual(expectedValue, refreshed.GetProperty("result").GetString());
        Assert.AreEqual(expectedType, refreshed.GetProperty("type").GetString());
        await AssertMcpStructAssignmentIntegerAsync(client, session, generation, frameId,
            "localNumber", "43", cancellationToken).ConfigureAwait(false);
        string diagnostics = await mcp.DisconnectAsync(TimeSpan.FromSeconds(20), cancellationToken).ConfigureAwait(false);
        Assert.DoesNotContain("fail:", diagnostics, StringComparison.OrdinalIgnoreCase);
        await ProcessExitWaiter.WaitAsync(exit, TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(false);
    }
}
