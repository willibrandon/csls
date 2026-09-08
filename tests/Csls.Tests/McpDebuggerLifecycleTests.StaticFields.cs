using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Csls.Tests;

/// <summary>
/// Exercises static receiver inspection and mutation through the real MCP and private control transports.
/// </summary>
public sealed partial class McpDebuggerLifecycleTests
{
    /// <summary>
    /// Preserves static receiver identity while enforcing control grants, stop generations, and revocation.
    /// </summary>
    [TestMethod]
    [Timeout(60000, CooperativeCancellation = true)]
    public async Task McpStaticReceiverAssignmentRequiresControl()
    {
        string repository = EditorToolResolver.FindRepositoryRoot();
        string source = Path.Join(repository, "tests", "Csls.TestProcessHost", "DebuggerFixture.cs");
        int line = (await File.ReadAllLinesAsync(source, TestContext.CancellationToken).ConfigureAwait(false))
            .Select(static (text, index) => (Text: text, Line: index + 1))
            .Single(static item => item.Text.Contains("Console.Write(announcement);", StringComparison.Ordinal)).Line;
        DirectoryInfo directory = Directory.CreateTempSubdirectory("csls-mcp-static-fields-");
        try
        {
            McpProcessSession mcp = await StartMcpAsync(TestContext.CancellationToken).ConfigureAwait(false);
            await using ConfiguredAsyncDisposable cleanup = mcp.ConfigureAwait(false);
            JsonElement started = await StartTargetAsync(mcp.Client, repository, source, line,
                Path.Join(directory.FullName, "continue.signal"), TestContext.CancellationToken).ConfigureAwait(false);
            string session = started.GetProperty("debugSession").GetString()!;
            ProcessExitObservation exit = ProcessExitWaiter.Observe(started.GetProperty("processId").GetInt32());
            JsonElement stopped = await WaitForStoppedAsync(mcp.Client, session, TestContext.CancellationToken).ConfigureAwait(false);
            long generation = stopped.GetProperty("stopGeneration").GetInt64();
            JsonElement frame = await GetSourceFrameAsync(mcp.Client, session, generation,
                stopped.GetProperty("stoppedThreadId").GetInt32(), source, TestContext.CancellationToken).ConfigureAwait(false);
            int frameId = frame.GetProperty("id").GetInt32();
            int localsReference = await GetMcpStructAssignmentLocalsAsync(mcp.Client, session, generation, frameId,
                TestContext.CancellationToken).ConfigureAwait(false);
            const string Expression = "Csls.TestProcessHost.DebuggerProxyEvaluationCounters.s_current.Constructions";
            await AssertMcpStructAssignmentIntegerAsync(mcp.Client, session, generation, frameId, Expression, "1",
                TestContext.CancellationToken).ConfigureAwait(false);
            var arguments = new Dictionary<string, object?>
            {
                ["debugSession"] = session,
                ["stopGeneration"] = generation,
                ["frameId"] = frameId,
                ["expression"] = Expression,
                ["value"] = "7"
            };
            await AssertToolErrorAsync(mcp.Client, "debug_expression_set", arguments, "debugger_control_denied",
                TestContext.CancellationToken).ConfigureAwait(false);
            await AssertMcpStructAssignmentIntegerAsync(mcp.Client, session, generation, frameId,
                "localProxyCounters.Constructions", "1", TestContext.CancellationToken).ConfigureAwait(false);
            _ = await GrantAgentControlAsync(mcp.Client, session, durationSeconds: 60, TestContext.CancellationToken).ConfigureAwait(false);
            arguments["stopGeneration"] = generation + 1;
            await AssertToolErrorAsync(mcp.Client, "debug_expression_set", arguments, "debugger_stale_generation",
                TestContext.CancellationToken).ConfigureAwait(false);
            arguments["stopGeneration"] = generation;
            JsonElement assigned = await AssertResourceSubscriptionAsync(mcp.Client,
                $"csls://debug/variables/{session}/{generation}/{localsReference}",
                () => CallAsync(mcp.Client, "debug_expression_set", arguments, TestContext.CancellationToken),
                TestContext.CancellationToken).ConfigureAwait(false);
            Assert.AreEqual(generation, assigned.GetProperty("stopGeneration").GetInt64());
            Assert.IsFalse(assigned.GetProperty("targetCodeExecuted").GetBoolean());
            Assert.AreEqual("7", assigned.GetProperty("variable").GetProperty("value").GetString());
            await AssertMcpStructAssignmentIntegerAsync(mcp.Client, session, generation, frameId,
                "localProxyCounters.Constructions", "7", TestContext.CancellationToken).ConfigureAwait(false);
            _ = await CallAsync(mcp.Client, "debug_agent_control_set",
                new Dictionary<string, object?> { ["debugSession"] = session, ["enabled"] = false },
                TestContext.CancellationToken).ConfigureAwait(false);
            arguments["value"] = "9";
            await AssertToolErrorAsync(mcp.Client, "debug_expression_set", arguments, "debugger_control_denied",
                TestContext.CancellationToken).ConfigureAwait(false);
            await AssertMcpStructAssignmentIntegerAsync(mcp.Client, session, generation, frameId, Expression, "7",
                TestContext.CancellationToken).ConfigureAwait(false);
            _ = await CallAsync(mcp.Client, "debug_session_end", new Dictionary<string, object?> { ["debugSession"] = session },
                TestContext.CancellationToken).ConfigureAwait(false);
            await ProcessExitWaiter.WaitAsync(exit, TimeSpan.FromSeconds(10), TestContext.CancellationToken).ConfigureAwait(false);
        }
        finally
        {
            await DirectoryReleaseWaiter.DeleteAsync(directory.FullName, TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        }
        Assert.IsFalse(Directory.Exists(directory.FullName));
    }
}
