using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Csls.Tests;

/// <summary>
/// Verifies automatic evaluation policy and explicit agent authorization through MCP.
/// </summary>
public sealed partial class McpDebuggerLifecycleTests
{
    /// <summary>
    /// Retains automatic evaluation policy across restart while requiring agent authorization for target execution.
    /// </summary>
    /// <param name="allowImplicitFuncEval">Whether authorized inspection constructs debugger proxies.</param>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    [Timeout(60000, CooperativeCancellation = true)]
    public async Task McpImplicitEvaluationPolicySurvivesRestart(bool allowImplicitFuncEval)
    {
        string repository = EditorToolResolver.FindRepositoryRoot();
        string source = Path.Join(repository, "tests", "Csls.TestProcessHost", "DebuggerFixture.cs");
        int line = (await File.ReadAllLinesAsync(source, TestContext.CancellationToken).ConfigureAwait(false))
            .Select(static (text, index) => (Text: text, Line: index + 1))
            .Single(static item => item.Text.Contains("Console.Write(announcement);", StringComparison.Ordinal)).Line;
        DirectoryInfo directory = Directory.CreateTempSubdirectory("csls-mcp-implicit-evaluation-");
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
                ["expressionEvaluationOptions"] = new Dictionary<string, object?> { ["allowImplicitFuncEval"] = allowImplicitFuncEval }
            }, TestContext.CancellationToken).ConfigureAwait(false);
            string session = started.GetProperty("debugSession").GetString()!;
            ProcessExitObservation exit = ProcessExitWaiter.Observe(started.GetProperty("processId").GetInt32());
            for (int incarnation = 0; incarnation < 2; incarnation++)
            {
                JsonElement stopped = await WaitForStoppedAsync(mcp.Client, session, TestContext.CancellationToken).ConfigureAwait(false);
                long generation = stopped.GetProperty("stopGeneration").GetInt64();
                int thread = stopped.GetProperty("stoppedThreadId").GetInt32();
                JsonElement frame = await GetSourceFrameAsync(mcp.Client, session, generation, thread, source,
                    TestContext.CancellationToken).ConfigureAwait(false);
                int frameId = frame.GetProperty("id").GetInt32();
                JsonElement proxy = await EvaluateMcpStructAssignmentAsync(mcp.Client, session, generation, frameId,
                    "localProxy", TestContext.CancellationToken).ConfigureAwait(false);
                var inspect = new Dictionary<string, object?>
                {
                    ["debugSession"] = session,
                    ["stopGeneration"] = generation,
                    ["variablesReference"] = proxy.GetProperty("variablesReference").GetInt32()
                };
                await AssertToolErrorAsync(mcp.Client, "debug_variables_get_presented", inspect,
                    "debugger_control_denied", TestContext.CancellationToken).ConfigureAwait(false);
                JsonElement observed = await CallAsync(mcp.Client, "debug_variables_get", inspect,
                    TestContext.CancellationToken).ConfigureAwait(false);
                Assert.AreEqual("41", Assert.ContainsSingle(observed.GetProperty("variables").EnumerateArray())
                    .GetProperty("value").GetString());
                await AssertMcpStructAssignmentIntegerAsync(mcp.Client, session, generation, frameId,
                    "localProxyCounters.Constructions", "1", TestContext.CancellationToken)
                    .ConfigureAwait(false);
                _ = await GrantAgentControlAsync(mcp.Client, session, durationSeconds: 60, TestContext.CancellationToken)
                    .ConfigureAwait(false);
                JsonElement presented = await CallAsync(mcp.Client, "debug_variables_get_presented", inspect,
                    TestContext.CancellationToken).ConfigureAwait(false);
                long presentedGeneration = presented.GetProperty("stopGeneration").GetInt64();
                if (allowImplicitFuncEval)
                {
                    Assert.IsGreaterThan(generation, presentedGeneration);
                    Assert.AreEqual("52", presented.GetProperty("variables").EnumerateArray()
                        .Single(value => value.GetProperty("name").GetString() == "ComputedValue")
                        .GetProperty("value").GetString());
                }
                else
                {
                    Assert.AreEqual(generation, presentedGeneration);
                    JsonElement field = Assert.ContainsSingle(presented.GetProperty("variables").EnumerateArray());
                    Assert.AreEqual("_rawValue", field.GetProperty("name").GetString());
                    Assert.AreEqual("41", field.GetProperty("value").GetString());
                }
                frame = await GetSourceFrameAsync(mcp.Client, session, presentedGeneration, thread, source,
                    TestContext.CancellationToken).ConfigureAwait(false);
                frameId = frame.GetProperty("id").GetInt32();
                foreach ((string counter, string expected) in new[]
                {
                    ("Constructions", allowImplicitFuncEval ? "2" : "1"),
                    ("GetterCalls", allowImplicitFuncEval ? "1" : "0")
                })
                {
                    await AssertMcpStructAssignmentIntegerAsync(mcp.Client, session, presentedGeneration, frameId,
                        $"localProxyCounters.{counter}", expected,
                        TestContext.CancellationToken).ConfigureAwait(false);
                    await AssertMcpStructAssignmentIntegerAsync(mcp.Client, session, presentedGeneration, frameId,
                        $"Csls.TestProcessHost.DebuggerProxyEvaluationCounters.s_current.{counter}", expected,
                        TestContext.CancellationToken).ConfigureAwait(false);
                }
                if (incarnation == 0)
                {
                    JsonElement restarted = await CallAsync(mcp.Client, "debug_session_restart", new Dictionary<string, object?>
                    {
                        ["debugSession"] = session,
                        ["stopGeneration"] = presentedGeneration
                    }, TestContext.CancellationToken).ConfigureAwait(false);
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
                    JsonElement executed = await CallAsync(mcp.Client, "debug_execute_expression", new Dictionary<string, object?>
                    {
                        ["debugSession"] = session,
                        ["stopGeneration"] = presentedGeneration,
                        ["frameId"] = frameId,
                        ["expression"] = "localObject.NextNumber()"
                    }, TestContext.CancellationToken).ConfigureAwait(false);
                    Assert.AreEqual("43", executed.GetProperty("evaluation").GetProperty("result").GetString());
                    Assert.IsTrue(executed.GetProperty("evaluation").GetProperty("targetCodeExecuted").GetBoolean());
                    Assert.IsGreaterThan(presentedGeneration, executed.GetProperty("stopGeneration").GetInt64());
                }
            }
            _ = await CallAsync(mcp.Client, "debug_session_end",
                new Dictionary<string, object?> { ["debugSession"] = session }, TestContext.CancellationToken).ConfigureAwait(false);
            await ProcessExitWaiter.WaitAsync(exit, TimeSpan.FromSeconds(10), TestContext.CancellationToken).ConfigureAwait(false);
        }
        finally
        {
            await DirectoryReleaseWaiter.DeleteAsync(directory.FullName, TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        }
    }
}
