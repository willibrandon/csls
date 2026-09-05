using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Csls.Tests;

/// <summary>
/// Verifies entry-stop semantics through real MCP and debugger worker transports.
/// </summary>
public sealed partial class McpDebuggerLifecycleTests
{
    private static readonly string[] s_entryPointArguments = ["--print-environment", "CSLS_ENTRY_RESULT"];

    /// <summary>
    /// Stops at authored entry, preserves observation-only access, and rearms on authorized restart.
    /// </summary>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    [Timeout(60000, CooperativeCancellation = true)]
    public async Task McpStopAtEntryPreservesAuthorizationAndRestart(bool restart)
    {
        string repositoryRoot = EditorToolResolver.FindRepositoryRoot();
        string sourcePath = Path.Join(repositoryRoot, "tests", "Csls.TestProcessHost", "Program.cs");
        int entryLine = (await File.ReadAllLinesAsync(sourcePath, TestContext.CancellationToken)
            .ConfigureAwait(false)).Select(static (line, index) => (Line: line, Number: index + 1))
            .Single(static candidate => candidate.Line.Contains(
                "if (args is [\"--unix-wait-status-fixture\"", StringComparison.Ordinal)).Number;
        McpProcessSession mcp = await StartMcpAsync(TestContext.CancellationToken).ConfigureAwait(false);
        await using ConfiguredAsyncDisposable cleanup = mcp.ConfigureAwait(false);
        JsonElement started = await CallAsync(mcp.Client, "debug_session_start",
            new Dictionary<string, object?>
            {
                ["program"] = EditorToolResolver.ResolveTestProcessHost(repositoryRoot),
                ["workingDirectory"] = repositoryRoot,
                ["arguments"] = s_entryPointArguments,
                ["environment"] = new Dictionary<string, string> { ["CSLS_ENTRY_RESULT"] = "entry-result" },
                ["sourceFileMap"] = new Dictionary<string, string> { ["/_/"] = repositoryRoot },
                ["stopAtEntry"] = true
            }, TestContext.CancellationToken).ConfigureAwait(false);
        string debugSession = started.GetProperty("debugSession").GetString()
            ?? throw new AssertFailedException("The launch omitted its debugger identity.");
        int processId = started.GetProperty("processId").GetInt32();
        ProcessExitObservation targetExit = ProcessExitWaiter.Observe(processId);
        JsonElement stopped = await WaitForStoppedAsync(mcp.Client, debugSession, TestContext.CancellationToken)
            .ConfigureAwait(false);
        Assert.AreEqual("entry", stopped.GetProperty("stopReason").GetString());
        Assert.IsFalse(stopped.GetProperty("agentControl").GetBoolean());
        long generation = stopped.GetProperty("stopGeneration").GetInt64();
        JsonElement frame = await ReadMcpEntryFrameAsync(mcp, stopped, sourcePath, entryLine).ConfigureAwait(false);
        var execution = new Dictionary<string, object?>
        {
            ["debugSession"] = debugSession,
            ["stopGeneration"] = generation,
            ["operation"] = "continue"
        };
        await AssertToolErrorAsync(mcp.Client, "debug_execution_control", execution,
            "debugger_control_denied", TestContext.CancellationToken).ConfigureAwait(false);
        JsonElement output = await CallAsync(mcp.Client, "debug_output_get",
            new Dictionary<string, object?> { ["debugSession"] = debugSession },
            TestContext.CancellationToken).ConfigureAwait(false);
        Assert.IsEmpty(output.GetProperty("entries").EnumerateArray());
        _ = await GrantAgentControlAsync(mcp.Client, debugSession, durationSeconds: 60,
            TestContext.CancellationToken).ConfigureAwait(false);

        if (restart)
        {
            JsonElement restarted = await CallAsync(mcp.Client, "debug_session_restart",
                new Dictionary<string, object?>
                {
                    ["debugSession"] = debugSession,
                    ["stopGeneration"] = generation
                }, TestContext.CancellationToken).ConfigureAwait(false);
            Assert.AreEqual(debugSession, restarted.GetProperty("debugSession").GetString());
            int replacementId = restarted.GetProperty("processId").GetInt32();
            Assert.AreNotEqual(processId, replacementId);
            await ProcessExitWaiter.WaitAsync(targetExit, TimeSpan.FromSeconds(10),
                TestContext.CancellationToken).ConfigureAwait(false);
            targetExit = ProcessExitWaiter.Observe(replacementId);
            stopped = await WaitForStoppedAsync(mcp.Client, debugSession, TestContext.CancellationToken)
                .ConfigureAwait(false);
            Assert.AreEqual("entry", stopped.GetProperty("stopReason").GetString());
            Assert.IsGreaterThan(generation, stopped.GetProperty("stopGeneration").GetInt64());
            await AssertToolErrorAsync(mcp.Client, "debug_scopes_get",
                new Dictionary<string, object?>
                {
                    ["debugSession"] = debugSession,
                    ["stopGeneration"] = generation,
                    ["frameId"] = frame.GetProperty("id").GetInt32()
                }, "debugger_stale_generation", TestContext.CancellationToken).ConfigureAwait(false);
            _ = await ReadMcpEntryFrameAsync(mcp, stopped, sourcePath, entryLine).ConfigureAwait(false);
            execution["stopGeneration"] = stopped.GetProperty("stopGeneration").GetInt64();
        }

        _ = await CallAsync(mcp.Client, "debug_execution_control", execution, TestContext.CancellationToken)
            .ConfigureAwait(false);
        await ProcessExitWaiter.WaitAsync(targetExit, TimeSpan.FromSeconds(10), TestContext.CancellationToken)
            .ConfigureAwait(false);
        output = await CallAsync(mcp.Client, "debug_output_get",
            new Dictionary<string, object?> { ["debugSession"] = debugSession },
            TestContext.CancellationToken).ConfigureAwait(false);
        JsonElement entry = Assert.ContainsSingle(output.GetProperty("entries").EnumerateArray());
        Assert.AreEqual("entry-result", entry.GetProperty("output").GetString());
        Assert.AreEqual("standardOutput", entry.GetProperty("category").GetString());
        string diagnostics = await mcp.DisconnectAsync(TimeSpan.FromSeconds(20), TestContext.CancellationToken)
            .ConfigureAwait(false);
        Assert.DoesNotContain("fail:", diagnostics, StringComparison.OrdinalIgnoreCase);
    }

    private async Task<JsonElement> ReadMcpEntryFrameAsync(
        McpProcessSession mcp, JsonElement stopped, string sourcePath, int entryLine)
    {
        long generation = stopped.GetProperty("stopGeneration").GetInt64();
        JsonElement stack = await CallAsync(mcp.Client, "debug_stack_get",
            new Dictionary<string, object?>
            {
                ["debugSession"] = stopped.GetProperty("debugSession").GetString(),
                ["stopGeneration"] = generation,
                ["threadId"] = stopped.GetProperty("stoppedThreadId").GetInt32(),
                ["levels"] = 1
            }, TestContext.CancellationToken).ConfigureAwait(false);
        Assert.AreEqual(generation, stack.GetProperty("stopGeneration").GetInt64());
        JsonElement frame = Assert.ContainsSingle(stack.GetProperty("stackFrames").EnumerateArray());
        Assert.AreEqual(sourcePath, frame.GetProperty("source").GetProperty("path").GetString());
        Assert.AreEqual(entryLine, frame.GetProperty("line").GetInt32());
        var frameArguments = new Dictionary<string, object?>
        {
            ["debugSession"] = stopped.GetProperty("debugSession").GetString(),
            ["stopGeneration"] = generation,
            ["frameId"] = frame.GetProperty("id").GetInt32()
        };
        JsonElement scopes = await CallAsync(mcp.Client, "debug_scopes_get", frameArguments,
            TestContext.CancellationToken).ConfigureAwait(false);
        JsonElement argumentsScope = Assert.ContainsSingle(scopes.GetProperty("scopes").EnumerateArray()
            .Where(scope => scope.GetProperty("name").GetString() == "Arguments"));
        JsonElement variables = await CallAsync(mcp.Client, "debug_variables_get",
            new Dictionary<string, object?>
            {
                ["debugSession"] = stopped.GetProperty("debugSession").GetString(),
                ["stopGeneration"] = generation,
                ["variablesReference"] = argumentsScope.GetProperty("variablesReference").GetInt32()
            }, TestContext.CancellationToken).ConfigureAwait(false);
        Assert.AreEqual(generation, variables.GetProperty("stopGeneration").GetInt64());
        JsonElement argument = Assert.ContainsSingle(variables.GetProperty("variables").EnumerateArray());
        Assert.AreEqual("args", argument.GetProperty("name").GetString());
        Assert.AreEqual("string[]", argument.GetProperty("type").GetString());
        Assert.AreEqual("args", argument.GetProperty("evaluateName").GetString());
        frameArguments["expression"] = "args[1]";
        JsonElement evaluation = await CallAsync(mcp.Client, "debug_evaluate", frameArguments,
            TestContext.CancellationToken).ConfigureAwait(false);
        Assert.AreEqual(generation, evaluation.GetProperty("stopGeneration").GetInt64());
        Assert.AreEqual("\"CSLS_ENTRY_RESULT\"", evaluation.GetProperty("evaluation").GetProperty("result").GetString());
        return frame;
    }
}
