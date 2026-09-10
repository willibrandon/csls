using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Csls.Tests;

/// <summary>
/// Verifies optimized source-binding diagnostics through MCP tools and subscribed resources.
/// </summary>
public sealed partial class McpDebuggerLifecycleTests
{
    /// <summary>
    /// Reports an unbindable Release source statement while preserving the stopped target and its resource state.
    /// </summary>
    [TestMethod]
    [Timeout(60000, CooperativeCancellation = true)]
    public async Task McpRejectedSourceBindingUpdatesResourceAndPreservesStop()
    {
        string repository = EditorToolResolver.FindRepositoryRoot();
        string source = Path.Join(repository, "tests", "Csls.TestProcessHost", "ReferenceAssignmentFixture.cs");
        string[] lines = await File.ReadAllLinesAsync(source, TestContext.CancellationToken).ConfigureAwait(false);
        int entryLine = lines.Select(static (text, index) => (Text: text, Line: index + 1))
            .Single(static line => line.Text.Contains("TBase genericTarget = genericBase;", StringComparison.Ordinal)).Line;
        int failedLine = lines.Select(static (text, index) => (Text: text, Line: index + 1))
            .Single(static line => line.Text.Contains("int result = DebuggerFixture.WaitForSignal(", StringComparison.Ordinal)).Line;
        string program = Path.Join(EditorToolResolver.ResolveArtifactsRoot(repository), "bin", "Csls.TestProcessHost",
            "release", "csls-test-process-host.dll");
        DirectoryInfo directory = Directory.CreateTempSubdirectory("csls-mcp-binding-failure-");
        try
        {
            McpProcessSession mcp = await StartMcpAsync(TestContext.CancellationToken).ConfigureAwait(false);
            await using ConfiguredAsyncDisposable cleanup = mcp.ConfigureAwait(false);
            JsonElement started = await CallAsync(mcp.Client, "debug_session_start", new Dictionary<string, object?>
            {
                ["program"] = program,
                ["workingDirectory"] = repository,
                ["arguments"] = new[] { "--debugger-reference-assignment-fixture", Path.Join(directory.FullName, "continue.signal") },
                ["sourceFileMap"] = new Dictionary<string, string> { ["/_/"] = repository },
                ["initialSourcePath"] = source,
                ["initialLine"] = entryLine,
                ["suppressJitOptimizations"] = true
            }, TestContext.CancellationToken).ConfigureAwait(false);
            string session = started.GetProperty("debugSession").GetString()!;
            ProcessExitObservation exit = ProcessExitWaiter.Observe(started.GetProperty("processId").GetInt32());
            JsonElement stopped = await WaitForStoppedAsync(mcp.Client, session, TestContext.CancellationToken).ConfigureAwait(false);
            Assert.AreEqual("breakpoint", stopped.GetProperty("stopReason").GetString());
            long generation = stopped.GetProperty("stopGeneration").GetInt64();
            int threadId = stopped.GetProperty("stoppedThreadId").GetInt32();
            JsonElement frame = await GetSourceFrameAsync(mcp.Client, session, generation, threadId, source,
                TestContext.CancellationToken).ConfigureAwait(false);
            Assert.AreEqual(entryLine, frame.GetProperty("line").GetInt32());
            _ = await GrantAgentControlAsync(mcp.Client, session, durationSeconds: 60, TestContext.CancellationToken)
                .ConfigureAwait(false);
            string resourceUri = $"csls://debug/breakpoints/{session}";
            JsonElement replaced = await AssertResourceSubscriptionAsync(mcp.Client, resourceUri,
                () => CallAsync(mcp.Client, "debug_source_breakpoints_set", new Dictionary<string, object?>
                {
                    ["debugSession"] = session,
                    ["stopGeneration"] = generation,
                    ["sourcePath"] = source,
                    ["breakpoints"] = new[] { new Dictionary<string, object?> { ["line"] = failedLine } }
                }, TestContext.CancellationToken), TestContext.CancellationToken).ConfigureAwait(false);
            JsonElement failure = Assert.ContainsSingle(replaced.GetProperty("breakpoints").EnumerateArray());
            Assert.IsFalse(failure.GetProperty("verified").GetBoolean());
            Assert.AreEqual(failedLine, failure.GetProperty("line").GetInt32());
            Assert.Contains("executable instruction", failure.GetProperty("message").GetString()!);
            JsonElement tool = await CallAsync(mcp.Client, "debug_breakpoints_get",
                new Dictionary<string, object?> { ["debugSession"] = session }, TestContext.CancellationToken).ConfigureAwait(false);
            JsonElement resource = await ReadAsync(mcp.Client, resourceUri, TestContext.CancellationToken).ConfigureAwait(false);
            foreach (JsonElement snapshot in new[] { tool, resource })
            {
                Assert.AreEqual(session, snapshot.GetProperty("debugSession").GetString());
                Assert.AreEqual("stopped", snapshot.GetProperty("state").GetString());
                Assert.AreEqual(generation, snapshot.GetProperty("stopGeneration").GetInt64());
                JsonElement breakpoint = Assert.ContainsSingle(snapshot.GetProperty("sourceBreakpoints").EnumerateArray());
                Assert.AreEqual(failure.GetProperty("id").GetInt32(), breakpoint.GetProperty("id").GetInt32());
                Assert.AreEqual(source, breakpoint.GetProperty("sourcePath").GetString());
                Assert.IsFalse(breakpoint.GetProperty("verified").GetBoolean());
                Assert.AreEqual(failedLine, breakpoint.GetProperty("line").GetInt32());
                Assert.AreEqual(failure.GetProperty("message").GetString(), breakpoint.GetProperty("message").GetString());
            }

            JsonElement refreshed = await GetSourceFrameAsync(mcp.Client, session, generation, threadId, source,
                TestContext.CancellationToken).ConfigureAwait(false);
            Assert.AreEqual(frame.GetProperty("id").GetInt32(), refreshed.GetProperty("id").GetInt32());
            Assert.AreEqual(entryLine, refreshed.GetProperty("line").GetInt32());
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
