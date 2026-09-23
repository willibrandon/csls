using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Csls.Tests;

/// <summary>
/// Verifies source checksum policy through real MCP launch, inspection, and authorized breakpoint tools.
/// </summary>
public sealed partial class McpDebuggerLifecycleTests
{
    /// <summary>
    /// Preserves source verification policy through the MCP broker and private debugger control transport.
    /// </summary>
    /// <param name="requireExactSource">The explicit policy or omission that selects the default.</param>
    [TestMethod]
    [DataRow(null)]
    [DataRow(true)]
    [DataRow(false)]
    [Timeout(60000, CooperativeCancellation = true)]
    public async Task McpSourceChecksumPolicyControlsBinding(bool? requireExactSource)
    {
        DirectoryInfo directory = Directory.CreateTempSubdirectory("csls-mcp-exact-source-");
        string sourcePath = Path.Join(directory.FullName, "Program.cs");
        try
        {
            string repository = EditorToolResolver.FindRepositoryRoot();
            string originalPath = Path.Join(repository, "tests", "Csls.TestProcessHost", "Program.cs");
            byte[] original = await File.ReadAllBytesAsync(originalPath, TestContext.CancellationToken).ConfigureAwait(false);
            await File.WriteAllBytesAsync(sourcePath, original, TestContext.CancellationToken).ConfigureAwait(false);
            await File.AppendAllTextAsync(sourcePath, "\n// Changed after compilation.\n", TestContext.CancellationToken).ConfigureAwait(false);
            int line = (await File.ReadAllLinesAsync(originalPath, TestContext.CancellationToken).ConfigureAwait(false))
                .Select(static (text, index) => (Text: text, Line: index + 1))
                .Single(static item => item.Text.Contains("if (args is [\"--unix-wait-status-fixture\"", StringComparison.Ordinal)).Line;
            McpProcessSession mcp = await StartMcpAsync(TestContext.CancellationToken).ConfigureAwait(false);
            await using ConfiguredAsyncDisposable cleanup = mcp.ConfigureAwait(false);
            var arguments = new Dictionary<string, object?>
            {
                ["program"] = EditorToolResolver.ResolveTestProcessHost(repository),
                ["workingDirectory"] = directory.FullName,
                ["stopAtEntry"] = true,
                ["sourceFileMap"] = new Dictionary<string, string>
                {
                    ["/_/"] = repository,
                    ["/_/tests/Csls.TestProcessHost/Program.cs"] = sourcePath,
                    [originalPath] = sourcePath
                }
            };
            if (requireExactSource is bool exactSource)
            {
                arguments["requireExactSource"] = exactSource;
            }

            JsonElement started = await CallAsync(mcp.Client, "debug_session_start", arguments, TestContext.CancellationToken).ConfigureAwait(false);
            string session = started.GetProperty("debugSession").GetString()!;
            ProcessExitObservation exit = ProcessExitWaiter.Observe(started.GetProperty("processId").GetInt32());
            JsonElement stopped = await WaitForStoppedAsync(mcp.Client, session, TestContext.CancellationToken).ConfigureAwait(false);
            long generation = stopped.GetProperty("stopGeneration").GetInt64();
            Assert.AreEqual("entry", stopped.GetProperty("stopReason").GetString());
            Assert.IsFalse(stopped.GetProperty("agentControl").GetBoolean());
            JsonElement stack = await CallAsync(mcp.Client, "debug_stack_get", new Dictionary<string, object?>
            {
                ["debugSession"] = session,
                ["stopGeneration"] = generation,
                ["threadId"] = stopped.GetProperty("stoppedThreadId").GetInt32(),
                ["levels"] = 1
            }, TestContext.CancellationToken).ConfigureAwait(false);
            JsonElement source = Assert.ContainsSingle(stack.GetProperty("stackFrames").EnumerateArray()).GetProperty("source");
            if (requireExactSource == false)
            {
                Assert.AreEqual(sourcePath, source.GetProperty("path").GetString());
                Assert.Contains("unverified local source", source.GetProperty("origin").GetString()!);
                Assert.IsFalse(source.TryGetProperty("checksum", out _));
            }
            else
            {
                Assert.IsFalse(source.TryGetProperty("path", out _));
            }

            var breakpoints = new Dictionary<string, object?>
            {
                ["debugSession"] = session,
                ["stopGeneration"] = generation,
                ["sourcePath"] = sourcePath,
                ["breakpoints"] = new[] { new Dictionary<string, object?> { ["line"] = line } }
            };
            await AssertToolErrorAsync(mcp.Client, "debug_source_breakpoints_set", breakpoints,
                "debugger_control_denied", TestContext.CancellationToken).ConfigureAwait(false);
            _ = await GrantAgentControlAsync(mcp.Client, session, durationSeconds: 60, TestContext.CancellationToken).ConfigureAwait(false);
            JsonElement bound = await CallAsync(mcp.Client, "debug_source_breakpoints_set", breakpoints, TestContext.CancellationToken)
                .ConfigureAwait(false);
            Assert.AreEqual(requireExactSource == false, Assert.ContainsSingle(bound.GetProperty("breakpoints").EnumerateArray())
                .GetProperty("verified").GetBoolean(), bound.GetRawText());
            JsonElement ended = await CallAsync(mcp.Client, "debug_session_end",
                new Dictionary<string, object?> { ["debugSession"] = session }, TestContext.CancellationToken).ConfigureAwait(false);
            Assert.AreEqual("terminated", ended.GetProperty("state").GetString());
            await ProcessExitWaiter.WaitAsync(exit, TimeSpan.FromSeconds(10), TestContext.CancellationToken).ConfigureAwait(false);
        }
        finally
        {
            File.Delete(sourcePath);
            directory.Delete();
        }
    }

    /// <summary>
    /// Applies source policy immediately after attaching and preserves independent target ownership.
    /// </summary>
    /// <param name="requireExactSource">The explicit policy or omission that selects the default.</param>
    [TestMethod]
    [DataRow(null)]
    [DataRow(true)]
    [DataRow(false)]
    [Timeout(60000, CooperativeCancellation = true)]
    public async Task McpAttachSourceChecksumPolicyControlsBinding(bool? requireExactSource)
    {
        DirectoryInfo directory = Directory.CreateTempSubdirectory("csls-mcp-attach-source-");
        string sourcePath = Path.Join(directory.FullName, "Program.cs");
        string finishPath = Path.Join(directory.FullName, "finish.signal");
        try
        {
            string repository = EditorToolResolver.FindRepositoryRoot();
            string originalPath = Path.Join(repository, "tests", "Csls.TestProcessHost", "Program.cs");
            string original = await File.ReadAllTextAsync(originalPath, TestContext.CancellationToken).ConfigureAwait(false);
            await File.WriteAllTextAsync(sourcePath, original + "\n// Changed after compilation.\n", TestContext.CancellationToken)
                .ConfigureAwait(false);
            int line = original.Split('\n').Select(static (text, index) => (Text: text, Line: index + 1))
                .Single(static item => item.Text.Contains("if (args is [\"--unix-wait-status-fixture\"", StringComparison.Ordinal)).Line;
            using Process target = StartDumpTarget(repository, finishPath);
            try
            {
                await WaitForReadyAsync(target, TestContext.CancellationToken).ConfigureAwait(false);
                McpProcessSession mcp = await StartMcpAsync(TestContext.CancellationToken).ConfigureAwait(false);
                await using ConfiguredAsyncDisposable cleanup = mcp.ConfigureAwait(false);
                var arguments = new Dictionary<string, object?>
                {
                    ["processId"] = target.Id,
                    ["sourceFileMap"] = new Dictionary<string, string>
                    {
                        ["/_/"] = repository,
                        ["/_/tests/Csls.TestProcessHost/Program.cs"] = sourcePath,
                        [originalPath] = sourcePath
                    }
                };
                if (requireExactSource is bool exactSource)
                {
                    arguments["requireExactSource"] = exactSource;
                }

                JsonElement attached = await CallAsync(mcp.Client, "debug_session_attach", arguments, TestContext.CancellationToken)
                    .ConfigureAwait(false);
                string session = attached.GetProperty("debugSession").GetString()!;
                Assert.AreEqual(target.Id, attached.GetProperty("processId").GetInt32());
                Assert.AreEqual("stopped", attached.GetProperty("state").GetString());
                Assert.IsFalse(attached.GetProperty("agentControl").GetBoolean());
                _ = await GrantAgentControlAsync(mcp.Client, session, durationSeconds: 60, TestContext.CancellationToken).ConfigureAwait(false);
                JsonElement result = await CallAsync(mcp.Client, "debug_source_breakpoints_set", new Dictionary<string, object?>
                {
                    ["debugSession"] = session,
                    ["stopGeneration"] = attached.GetProperty("stopGeneration").GetInt64(),
                    ["sourcePath"] = sourcePath,
                    ["breakpoints"] = new[] { new Dictionary<string, object?> { ["line"] = line } }
                }, TestContext.CancellationToken).ConfigureAwait(false);
                JsonElement breakpoint = Assert.ContainsSingle(result.GetProperty("breakpoints").EnumerateArray());
                Assert.AreEqual(requireExactSource == false, breakpoint.GetProperty("verified").GetBoolean(), breakpoint.GetRawText());
                if (requireExactSource != false)
                {
                    Assert.Contains("source file differs", breakpoint.GetProperty("message").GetString()!);
                }

                JsonElement ended = await CallAsync(mcp.Client, "debug_session_end",
                    new Dictionary<string, object?> { ["debugSession"] = session }, TestContext.CancellationToken).ConfigureAwait(false);
                Assert.AreEqual("terminated", ended.GetProperty("state").GetString());
                Assert.IsFalse(target.HasExited);
                await File.WriteAllTextAsync(finishPath, string.Empty, TestContext.CancellationToken).ConfigureAwait(false);
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
            File.Delete(sourcePath);
            File.Delete(finishPath);
            directory.Delete();
        }
    }
}
