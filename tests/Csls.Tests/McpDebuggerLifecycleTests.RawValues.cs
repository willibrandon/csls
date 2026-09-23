using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Csls.Tests;

/// <summary>
/// Verifies raw value presentation and observation-only authorization over the MCP worker transport.
/// </summary>
public sealed partial class McpDebuggerLifecycleTests
{
    /// <summary>
    /// Applies raw presentation while attaching through MCP and preserves independent process ownership.
    /// </summary>
    [TestMethod]
    [Timeout(60000, CooperativeCancellation = true)]
    public async Task McpAttachRawValuesPreserveInspectionAndTargetOwnership()
    {
        string repository = EditorToolResolver.FindRepositoryRoot();
        string source = Path.Join(repository, "tests", "Csls.TestProcessHost", "DebuggerFixture.cs");
        DirectoryInfo directory = Directory.CreateTempSubdirectory("csls-mcp-attach-raw-values-");
        string signal = Path.Join(directory.FullName, "continue.signal");
        try
        {
            using Process target = StartDumpTarget(repository, signal, captureFrameValues: true);
            try
            {
                await WaitForReadyAsync(target, TestContext.CancellationToken).ConfigureAwait(false);
                McpProcessSession mcp = await StartMcpAsync(TestContext.CancellationToken).ConfigureAwait(false);
                await using ConfiguredAsyncDisposable cleanup = mcp.ConfigureAwait(false);
                JsonElement attached = await CallAsync(mcp.Client, "debug_session_attach", new Dictionary<string, object?>
                {
                    ["processId"] = target.Id,
                    ["sourceFileMap"] = new Dictionary<string, string> { ["/_/"] = repository },
                    ["expressionEvaluationOptions"] = new Dictionary<string, object?> { ["showRawValues"] = true }
                }, TestContext.CancellationToken).ConfigureAwait(false);
                string session = attached.GetProperty("debugSession").GetString()!;
                Assert.AreEqual(target.Id, attached.GetProperty("processId").GetInt32());
                Assert.AreEqual("stopped", attached.GetProperty("state").GetString());
                Assert.IsFalse(attached.GetProperty("agentControl").GetBoolean());
                long generation = attached.GetProperty("stopGeneration").GetInt64();
                JsonElement frame = await GetRawValueFixtureFrameAsync(mcp, session, generation, source).ConfigureAwait(false);
                int frameId = frame.GetProperty("id").GetInt32();
                JsonElement proxy = await EvaluateMcpStructAssignmentAsync(mcp.Client, session, generation, frameId,
                    "localProxy", TestContext.CancellationToken).ConfigureAwait(false);
                JsonElement fields = await CallAsync(mcp.Client, "debug_variables_get", new Dictionary<string, object?>
                {
                    ["debugSession"] = session,
                    ["stopGeneration"] = generation,
                    ["variablesReference"] = proxy.GetProperty("variablesReference").GetInt32()
                }, TestContext.CancellationToken).ConfigureAwait(false);
                JsonElement field = Assert.ContainsSingle(fields.GetProperty("variables").EnumerateArray());
                Assert.AreEqual("_rawValue", field.GetProperty("name").GetString());
                Assert.AreEqual("41", field.GetProperty("value").GetString());
                Assert.AreEqual("localProxy._rawValue", field.GetProperty("evaluateName").GetString());
                JsonElement display = await EvaluateMcpStructAssignmentAsync(mcp.Client, session, generation, frameId,
                    "localDisplay", TestContext.CancellationToken).ConfigureAwait(false);
                Assert.AreEqual("{Csls.TestProcessHost.DebuggerDisplayFixture}", display.GetProperty("result").GetString());
                JsonElement state = await CallAsync(mcp.Client, "debug_session_get",
                    new Dictionary<string, object?> { ["debugSession"] = session }, TestContext.CancellationToken).ConfigureAwait(false);
                Assert.AreEqual(generation, state.GetProperty("stopGeneration").GetInt64());
                JsonElement ended = await CallAsync(mcp.Client, "debug_session_end",
                    new Dictionary<string, object?> { ["debugSession"] = session }, TestContext.CancellationToken).ConfigureAwait(false);
                Assert.AreEqual("terminated", ended.GetProperty("state").GetString());
                Assert.IsFalse(target.HasExited);
                await File.WriteAllTextAsync(signal, string.Empty, TestContext.CancellationToken).ConfigureAwait(false);
                await target.WaitForExitAsync(TestContext.CancellationToken).ConfigureAwait(false);
                Assert.AreEqual(0, target.ExitCode);
            }
            finally
            {
                if (!target.HasExited)
                {
                    target.Kill(entireProcessTree: true);
                    await target.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            await DirectoryReleaseWaiter.DeleteAsync(directory.FullName, TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        }
    }

    private async Task<JsonElement> GetRawValueFixtureFrameAsync(
        McpProcessSession mcp, string session, long generation, string source)
    {
        JsonElement threads = await CallAsync(mcp.Client, "debug_threads_get",
            new Dictionary<string, object?> { ["debugSession"] = session, ["stopGeneration"] = generation },
            TestContext.CancellationToken).ConfigureAwait(false);
        List<JsonElement> frames = [];
        foreach (JsonElement thread in threads.GetProperty("threads").EnumerateArray())
        {
            JsonElement stack = await CallAsync(mcp.Client, "debug_stack_get", new Dictionary<string, object?>
            {
                ["debugSession"] = session,
                ["stopGeneration"] = generation,
                ["threadId"] = thread.GetProperty("id").GetInt32(),
                ["levels"] = 64
            }, TestContext.CancellationToken).ConfigureAwait(false);
            frames.AddRange(stack.GetProperty("stackFrames").EnumerateArray().Where(frame =>
                frame.TryGetProperty("source", out JsonElement location) &&
                location.TryGetProperty("path", out JsonElement path) &&
                string.Equals(path.GetString(), source, StringComparison.Ordinal)));
        }
        return Assert.ContainsSingle(frames);
    }

    /// <summary>
    /// Preserves physical value inspection and explicit execution authorization across a restarted target.
    /// </summary>
    [TestMethod]
    [Timeout(60000, CooperativeCancellation = true)]
    public async Task McpRawValuesPreserveInspectionAndRestartAuthorization()
    {
        string repository = EditorToolResolver.FindRepositoryRoot();
        string source = Path.Join(repository, "tests", "Csls.TestProcessHost", "DebuggerFixture.cs");
        int line = (await File.ReadAllLinesAsync(source, TestContext.CancellationToken).ConfigureAwait(false))
            .Select(static (text, index) => (Text: text, Line: index + 1))
            .Single(static item => item.Text.Contains("Thread.Sleep(1);", StringComparison.Ordinal)).Line;
        DirectoryInfo directory = Directory.CreateTempSubdirectory("csls-mcp-raw-values-");
        try
        {
            McpProcessSession mcp = await StartMcpAsync(TestContext.CancellationToken).ConfigureAwait(false);
            await using ConfiguredAsyncDisposable cleanup = mcp.ConfigureAwait(false);
            JsonElement started = await CallAsync(mcp.Client, "debug_session_start", new Dictionary<string, object?>
            {
                ["program"] = EditorToolResolver.ResolveTestProcessHost(repository),
                ["workingDirectory"] = repository,
                ["arguments"] = new[] { "--debugger-fixture", Path.Join(directory.FullName, "continue.signal") },
                ["sourceFileMap"] = new Dictionary<string, string> { ["/_/"] = repository },
                ["initialSourcePath"] = source,
                ["initialLine"] = line,
                ["suppressJitOptimizations"] = true,
                ["expressionEvaluationOptions"] = new Dictionary<string, object?> { ["showRawValues"] = true }
            }, TestContext.CancellationToken).ConfigureAwait(false);
            string session = started.GetProperty("debugSession").GetString()!;
            ProcessExitObservation exit = ProcessExitWaiter.Observe(started.GetProperty("processId").GetInt32());
            long previousGeneration = 0;
            for (int incarnation = 0; incarnation < 2; incarnation++)
            {
                JsonElement stopped = await WaitForStoppedAsync(mcp.Client, session, TestContext.CancellationToken).ConfigureAwait(false);
                long generation = stopped.GetProperty("stopGeneration").GetInt64();
                Assert.IsGreaterThan(previousGeneration, generation);
                Assert.IsFalse(stopped.GetProperty("agentControl").GetBoolean());
                JsonElement frame = await GetSourceFrameAsync(mcp.Client, session, generation,
                    stopped.GetProperty("stoppedThreadId").GetInt32(), source, TestContext.CancellationToken).ConfigureAwait(false);
                int frameId = frame.GetProperty("id").GetInt32();
                JsonElement proxy = await EvaluateMcpStructAssignmentAsync(mcp.Client, session, generation, frameId,
                    "localProxy", TestContext.CancellationToken).ConfigureAwait(false);
                var inspect = new Dictionary<string, object?>
                {
                    ["debugSession"] = session,
                    ["stopGeneration"] = generation,
                    ["variablesReference"] = proxy.GetProperty("variablesReference").GetInt32()
                };
                JsonElement fields = await CallAsync(mcp.Client, "debug_variables_get", inspect, TestContext.CancellationToken)
                    .ConfigureAwait(false);
                JsonElement field = Assert.ContainsSingle(fields.GetProperty("variables").EnumerateArray());
                Assert.AreEqual("_rawValue", field.GetProperty("name").GetString());
                Assert.AreEqual("41", field.GetProperty("value").GetString());
                Assert.AreEqual("localProxy._rawValue", field.GetProperty("evaluateName").GetString());
                JsonElement display = await EvaluateMcpStructAssignmentAsync(mcp.Client, session, generation, frameId,
                    "localDisplay", TestContext.CancellationToken).ConfigureAwait(false);
                Assert.AreEqual("{Csls.TestProcessHost.DebuggerDisplayFixture}", display.GetProperty("result").GetString());
                await AssertMcpStructAssignmentIntegerAsync(mcp.Client, session, generation, frameId,
                    "localResultsView._enumerationCount", "0", TestContext.CancellationToken).ConfigureAwait(false);
                await AssertToolErrorAsync(mcp.Client, "debug_variables_get_presented", inspect,
                    "debugger_control_denied", TestContext.CancellationToken).ConfigureAwait(false);
                await AssertToolErrorAsync(mcp.Client, "debug_expression_set", new Dictionary<string, object?>
                {
                    ["debugSession"] = session,
                    ["stopGeneration"] = generation,
                    ["frameId"] = frameId,
                    ["expression"] = "localBrowsable._hidden",
                    ["value"] = "99"
                }, "debugger_control_denied", TestContext.CancellationToken).ConfigureAwait(false);
                var execution = new Dictionary<string, object?>
                {
                    ["debugSession"] = session,
                    ["stopGeneration"] = generation,
                    ["frameId"] = frameId,
                    ["expression"] = "localObject.NextNumber()"
                };
                await AssertToolErrorAsync(mcp.Client, "debug_execute_expression", execution,
                    "debugger_control_denied", TestContext.CancellationToken).ConfigureAwait(false);
                JsonElement state = await CallAsync(mcp.Client, "debug_session_get",
                    new Dictionary<string, object?> { ["debugSession"] = session }, TestContext.CancellationToken).ConfigureAwait(false);
                Assert.AreEqual(generation, state.GetProperty("stopGeneration").GetInt64());
                if (incarnation == 0)
                {
                    previousGeneration = generation;
                    var restartArguments = new Dictionary<string, object?>
                    {
                        ["debugSession"] = session,
                        ["stopGeneration"] = generation
                    };
                    await AssertToolErrorAsync(mcp.Client, "debug_session_restart", restartArguments,
                        "debugger_control_denied", TestContext.CancellationToken).ConfigureAwait(false);
                    _ = await GrantAgentControlAsync(mcp.Client, session, durationSeconds: 60, TestContext.CancellationToken)
                        .ConfigureAwait(false);
                    JsonElement restarted = await CallAsync(mcp.Client, "debug_session_restart",
                        restartArguments, TestContext.CancellationToken).ConfigureAwait(false);
                    await ProcessExitWaiter.WaitAsync(exit, TimeSpan.FromSeconds(10), TestContext.CancellationToken).ConfigureAwait(false);
                    exit = ProcessExitWaiter.Observe(restarted.GetProperty("processId").GetInt32());
                    _ = await WaitForStoppedAsync(mcp.Client, session, TestContext.CancellationToken).ConfigureAwait(false);
                    await AssertToolErrorAsync(mcp.Client, "debug_variables_get", inspect,
                        "debugger_stale_generation", TestContext.CancellationToken).ConfigureAwait(false);
                    _ = await CallAsync(mcp.Client, "debug_agent_control_set", new Dictionary<string, object?>
                    {
                        ["debugSession"] = session,
                        ["enabled"] = false
                    }, TestContext.CancellationToken).ConfigureAwait(false);
                }
                else
                {
                    _ = await GrantAgentControlAsync(mcp.Client, session, durationSeconds: 60, TestContext.CancellationToken)
                        .ConfigureAwait(false);
                    JsonElement executed = await CallAsync(mcp.Client, "debug_execute_expression", execution,
                        TestContext.CancellationToken).ConfigureAwait(false);
                    Assert.AreEqual("43", executed.GetProperty("evaluation").GetProperty("result").GetString());
                    Assert.IsTrue(executed.GetProperty("evaluation").GetProperty("targetCodeExecuted").GetBoolean());
                    Assert.IsGreaterThan(generation, executed.GetProperty("stopGeneration").GetInt64());
                }
            }

            JsonElement ended = await CallAsync(mcp.Client, "debug_session_end",
                new Dictionary<string, object?> { ["debugSession"] = session }, TestContext.CancellationToken).ConfigureAwait(false);
            Assert.AreEqual("terminated", ended.GetProperty("state").GetString());
            await ProcessExitWaiter.WaitAsync(exit, TimeSpan.FromSeconds(10), TestContext.CancellationToken).ConfigureAwait(false);
        }
        finally
        {
            await DirectoryReleaseWaiter.DeleteAsync(directory.FullName, TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        }
    }
}
