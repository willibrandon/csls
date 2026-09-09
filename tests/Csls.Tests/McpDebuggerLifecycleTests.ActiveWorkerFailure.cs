using ModelContextProtocol.Protocol;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Csls.Tests;

/// <summary>
/// Verifies worker loss while an MCP request is executing actual target code.
/// </summary>
public sealed partial class McpDebuggerLifecycleTests
{
    /// <summary>
    /// Returns the session's connection error to an active evaluation and keeps the MCP connection usable.
    /// </summary>
    /// <param name="assign">Whether the target call supplies an assignment rather than a standalone evaluation.</param>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    [OSCondition(OperatingSystems.Linux)]
    [Timeout(60000, CooperativeCancellation = true)]
    public async Task WorkerExitDuringEvaluationPreservesConnectionError(bool assign)
    {
        string repository = EditorToolResolver.FindRepositoryRoot();
        string source = Path.Join(repository, "tests", "Csls.TestProcessHost", "DebuggerFixture.cs");
        int line = (await File.ReadAllLinesAsync(source, TestContext.CancellationToken).ConfigureAwait(false))
            .Select(static (text, index) => (Text: text, Line: index + 1))
            .Single(static item => item.Text.Contains("Console.Write(announcement);", StringComparison.Ordinal)).Line;
        DirectoryInfo directory = Directory.CreateTempSubdirectory("csls-mcp-active-worker-failure-");
        try
        {
            string signal = Path.Join(directory.FullName, "continue.signal");
            McpProcessSession mcp = await StartMcpAsync(TestContext.CancellationToken).ConfigureAwait(false);
            await using ConfiguredAsyncDisposable cleanup = mcp.ConfigureAwait(false);
            JsonElement started = await StartTargetAsync(mcp.Client, repository, source, line, signal,
                TestContext.CancellationToken).ConfigureAwait(false);
            string session = started.GetProperty("debugSession").GetString()!;
            using var target = Process.GetProcessById(started.GetProperty("processId").GetInt32());
            _ = target.SafeHandle;
            using var evaluationLifetime = CancellationTokenSource.CreateLinkedTokenSource(TestContext.CancellationToken);
            Task<CallToolResult>? evaluation = null;
            try
            {
                JsonElement stopped = await WaitForStoppedAsync(mcp.Client, session, TestContext.CancellationToken)
                    .ConfigureAwait(false);
                Assert.AreEqual("breakpoint", stopped.GetProperty("stopReason").GetString());
                _ = await GrantAgentControlAsync(mcp.Client, session, durationSeconds: 60, TestContext.CancellationToken)
                    .ConfigureAwait(false);
                JsonElement frame = await GetSourceFrameAsync(mcp.Client, session,
                    stopped.GetProperty("stopGeneration").GetInt64(), stopped.GetProperty("stoppedThreadId").GetInt32(),
                    source, TestContext.CancellationToken).ConfigureAwait(false);
                using Process worker = await FindOwnedDebuggerWorkerAsync(target.Id, mcp.LauncherProcess.Id,
                    EditorToolResolver.ResolveDebuggerWorker(repository), TestContext.CancellationToken).ConfigureAwait(false);
                var arguments = new Dictionary<string, object?>
                {
                    ["debugSession"] = session,
                    ["stopGeneration"] = stopped.GetProperty("stopGeneration").GetInt64(),
                    ["frameId"] = frame.GetProperty("id").GetInt32(),
                    ["expression"] = assign ? "localNumber" : "localObject.WaitForDebuggerCancellation()"
                };
                if (assign)
                {
                    arguments["value"] = "localObject.WaitForDebuggerCancellation()";
                }
                evaluation = mcp.Client.CallToolAsync(assign ? "debug_expression_set" : "debug_execute_expression",
                    arguments, cancellationToken: evaluationLifetime.Token).AsTask();
                await FileTextWaiter.WaitAsync(signal + ".evaluation", "started", TimeSpan.FromSeconds(10),
                    TestContext.CancellationToken).ConfigureAwait(false);
                Assert.IsFalse(evaluation.IsCompleted, "The target must still be executing the requested call.");
                Assert.IsFalse(worker.HasExited);
                worker.Kill();
                await worker.WaitForExitAsync(TestContext.CancellationToken).ConfigureAwait(false);
                Assert.IsTrue(worker.HasExited);
                AssertDebuggerConnectionError(await evaluation.WaitAsync(TestContext.CancellationToken).ConfigureAwait(false),
                    session);
                Assert.IsFalse(evaluationLifetime.IsCancellationRequested);
                Assert.IsFalse(mcp.LauncherProcess.HasExited);
                CallToolResult ended = await mcp.Client.CallToolAsync("debug_session_end",
                    new Dictionary<string, object?> { ["debugSession"] = session },
                    cancellationToken: TestContext.CancellationToken).ConfigureAwait(false);
                AssertDebuggerConnectionError(ended, session);
                JsonElement empty = await CallAsync(mcp.Client, "debug_sessions_list", [], TestContext.CancellationToken)
                    .ConfigureAwait(false);
                Assert.IsEmpty(empty.GetProperty("sessions").EnumerateArray());
                JsonElement next = await StartTargetAsync(mcp.Client, repository, source, line,
                    Path.Join(directory.FullName, "next.signal"), TestContext.CancellationToken).ConfigureAwait(false);
                string nextSession = next.GetProperty("debugSession").GetString()!;
                Assert.AreNotEqual(session, nextSession);
                ProcessExitObservation nextExit = ProcessExitWaiter.Observe(next.GetProperty("processId").GetInt32());
                JsonElement nextStopped = await WaitForStoppedAsync(mcp.Client, nextSession, TestContext.CancellationToken)
                    .ConfigureAwait(false);
                Assert.AreEqual("breakpoint", nextStopped.GetProperty("stopReason").GetString());
                JsonElement nextEnded = await CallAsync(mcp.Client, "debug_session_end",
                    new Dictionary<string, object?> { ["debugSession"] = nextSession }, TestContext.CancellationToken)
                    .ConfigureAwait(false);
                Assert.AreEqual("terminated", nextEnded.GetProperty("state").GetString());
                await ProcessExitWaiter.WaitAsync(nextExit, TimeSpan.FromSeconds(10), TestContext.CancellationToken)
                    .ConfigureAwait(false);
            }
            finally
            {
                await evaluationLifetime.CancelAsync().ConfigureAwait(false);
                if (evaluation is not null)
                {
                    Task pending = evaluation;
                    await pending.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
                }
                if (!target.HasExited)
                {
                    target.Kill();
                }
                await target.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            }
        }
        finally
        {
            await DirectoryReleaseWaiter.DeleteAsync(directory.FullName, TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        }
    }
}
