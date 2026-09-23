using ModelContextProtocol.Client;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Csls.Tests;

/// <summary>
/// Verifies declared reference assignments through real MCP, control, and debugger processes.
/// </summary>
public sealed partial class McpDebuggerLifecycleTests
{
    /// <summary>
    /// Requires control and current generations while preserving reference conversions and resource updates.
    /// </summary>
    /// <param name="setVariable">Whether to assign through a locals container instead of an expression.</param>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    [Timeout(60000, CooperativeCancellation = true)]
    [OSCondition(ConditionMode.Exclude, OperatingSystems.Windows)]
    public async Task McpReferenceAssignmentUsesDeclaredTypes(bool setVariable)
    {
        string repositoryRoot = EditorToolResolver.FindRepositoryRoot();
        string sourcePath = Path.Join(repositoryRoot, "tests", "Csls.TestProcessHost", "ReferenceAssignmentFixture.cs");
        int line = (await File.ReadAllLinesAsync(sourcePath, TestContext.CancellationToken).ConfigureAwait(false))
            .Select(static (text, index) => (Text: text, Line: index + 1))
            .Single(static candidate => candidate.Text.Contains("int result = DebuggerFixture.WaitForSignal(",
                StringComparison.Ordinal)).Line;
        DirectoryInfo directory = Directory.CreateTempSubdirectory("csls-mcp-reference-assignment-");
        try
        {
            await ExerciseMcpReferenceAssignmentAsync(repositoryRoot, sourcePath, line, directory.FullName,
                setVariable, TestContext.CancellationToken).ConfigureAwait(false);
        }
        finally
        {
            await DirectoryReleaseWaiter.DeleteAsync(directory.FullName, TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        }
    }

    private static async Task ExerciseMcpReferenceAssignmentAsync(
        string repositoryRoot, string sourcePath, int line, string directory, bool setVariable,
        CancellationToken cancellationToken)
    {
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
        JsonElement frame = await GetSourceFrameAsync(client, session, generation,
            stopped.GetProperty("stoppedThreadId").GetInt32(), sourcePath, cancellationToken).ConfigureAwait(false);
        int frameId = frame.GetProperty("id").GetInt32();
        int localsReference = await GetMcpStructAssignmentLocalsAsync(
            client, session, generation, frameId, cancellationToken).ConfigureAwait(false);
        string tool = setVariable ? "debug_variable_set" : "debug_expression_set";
        string destinationKey = setVariable ? "name" : "expression";
        var arguments = new Dictionary<string, object?>
        {
            ["debugSession"] = session,
            ["stopGeneration"] = generation,
            ["value"] = "derivedSource",
            [destinationKey] = "baseTarget",
            [setVariable ? "variablesReference" : "frameId"] = setVariable ? localsReference : frameId
        };
        await AssertToolErrorAsync(client, tool, arguments, "debugger_control_denied", cancellationToken)
            .ConfigureAwait(false);
        await AssertMcpReferenceMessageAsync(client, session, generation, frameId, "baseTarget", "original base",
            cancellationToken).ConfigureAwait(false);
        JsonElement granted = await GrantAgentControlAsync(client, session, durationSeconds: 60, cancellationToken)
            .ConfigureAwait(false);
        Assert.AreEqual(generation, granted.GetProperty("stopGeneration").GetInt64());
        arguments["stopGeneration"] = checked(generation + 1);
        await AssertToolErrorAsync(client, tool, arguments, "debugger_stale_generation", cancellationToken)
            .ConfigureAwait(false);
        arguments["stopGeneration"] = generation;
        JsonElement assigned = await AssertResourceSubscriptionAsync(
            client, $"csls://debug/variables/{session}/{generation}/{localsReference}",
            () => CallAsync(client, tool, arguments, cancellationToken), cancellationToken).ConfigureAwait(false);
        Assert.AreEqual(session, assigned.GetProperty("debugSession").GetString());
        Assert.AreEqual(generation, assigned.GetProperty("stopGeneration").GetInt64());
        Assert.IsFalse(assigned.GetProperty("targetCodeExecuted").GetBoolean());
        JsonElement variable = assigned.GetProperty("variable");
        Assert.AreEqual("baseTarget", variable.GetProperty("evaluateName").GetString());
        Assert.AreEqual("System.ArgumentException", variable.GetProperty("type").GetString());
        JsonElement children = await ReadMcpStructAssignmentVariablesAsync(client, session, generation,
            variable.GetProperty("variablesReference").GetInt32(), cancellationToken).ConfigureAwait(false);
        JsonElement message = Assert.ContainsSingle(children.GetProperty("variables").EnumerateArray()
            .Where(static child => child.GetProperty("name").GetString() == "_message"));
        Assert.AreEqual("\"replacement\"", message.GetProperty("value").GetString());
        Assert.AreEqual("string", message.GetProperty("type").GetString());
        await AssertMcpReferenceMessageAsync(client, session, generation, frameId, "baseTarget", "replacement",
            cancellationToken).ConfigureAwait(false);

        arguments[destinationKey] = "derivedTarget";
        arguments["value"] = "widenedSource";
        await AssertToolErrorAsync(client, tool, arguments, "debugger_operation_failed", cancellationToken)
            .ConfigureAwait(false);
        await AssertMcpReferenceMessageAsync(client, session, generation, frameId, "derivedTarget", "original derived",
            cancellationToken).ConfigureAwait(false);
        arguments[destinationKey] = "target";
        arguments["value"] = "nullBaseTarget";
        await AssertToolErrorAsync(client, tool, arguments, "debugger_operation_failed", cancellationToken)
            .ConfigureAwait(false);
        JsonElement originalText = await EvaluateMcpStructAssignmentAsync(
            client, session, generation, frameId, "target", cancellationToken).ConfigureAwait(false);
        Assert.AreEqual("\"reference-assignment-value\"", originalText.GetProperty("result").GetString());
        Assert.AreEqual("string", originalText.GetProperty("type").GetString());
        JsonElement revoked = await CallAsync(client, "debug_agent_control_set",
            new Dictionary<string, object?> { ["debugSession"] = session, ["enabled"] = false }, cancellationToken)
            .ConfigureAwait(false);
        Assert.IsFalse(revoked.GetProperty("agentControl").GetBoolean());
        Assert.AreEqual(generation, revoked.GetProperty("stopGeneration").GetInt64());
        arguments[destinationKey] = "baseTarget";
        arguments["value"] = "null";
        await AssertToolErrorAsync(client, tool, arguments, "debugger_control_denied", cancellationToken)
            .ConfigureAwait(false);
        await AssertMcpReferenceMessageAsync(client, session, generation, frameId, "baseTarget", "replacement",
            cancellationToken).ConfigureAwait(false);
        string diagnostics = await mcp.DisconnectAsync(TimeSpan.FromSeconds(20), cancellationToken).ConfigureAwait(false);
        Assert.DoesNotContain("fail:", diagnostics, StringComparison.OrdinalIgnoreCase);
        await ProcessExitWaiter.WaitAsync(exit, TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(false);
    }

    private static async Task AssertMcpReferenceMessageAsync(
        McpClient client, string session, long generation, int frameId, string expression, string expected,
        CancellationToken cancellationToken)
    {
        JsonElement evaluated = await EvaluateMcpStructAssignmentAsync(
            client, session, generation, frameId, expression + "._message", cancellationToken).ConfigureAwait(false);
        Assert.AreEqual($"\"{expected}\"", evaluated.GetProperty("result").GetString());
        Assert.AreEqual("string", evaluated.GetProperty("type").GetString());
    }
}
