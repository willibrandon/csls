using Microsoft.Diagnostics.NETCore.Client;
using ModelContextProtocol;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Csls.Tests;

/// <summary>
/// Verifies offline managed process-dump inspection through real MCP workers.
/// </summary>
public sealed partial class McpDebuggerLifecycleTests
{
    /// <summary>
    /// Captures a real dump, stops its target, and inspects it through MCP.
    /// </summary>
    [TestMethod]
    [Timeout(60000, CooperativeCancellation = true)]
    public async Task DebuggerDumpSessionInspectsTerminatedManagedTarget()
    {
        string repositoryRoot = EditorToolResolver.FindRepositoryRoot();
        string testDirectory = Path.Join(
            Path.GetTempPath(),
            $"csls-debugger-dump-{Guid.NewGuid():N}");
        Directory.CreateDirectory(testDirectory);
        string dumpPath = Path.Join(testDirectory, "managed-target.dmp");
        string finishPath = Path.Join(testDirectory, "finish.signal");
        using Process target = StartDumpTarget(repositoryRoot, finishPath);
        try
        {
            await WaitForReadyAsync(target, TestContext.CancellationToken)
                .ConfigureAwait(false);
            var diagnostics = new DiagnosticsClient(target.Id);
            await diagnostics.WriteDumpAsync(
                DumpType.Triage,
                dumpPath,
                logDumpGeneration: false,
                TestContext.CancellationToken).ConfigureAwait(false);
            Assert.IsTrue(File.Exists(dumpPath));
            ProcessExitObservation exit = ProcessExitWaiter.Observe(target.Id);
            target.Kill(entireProcessTree: true);
            await ProcessExitWaiter.WaitAsync(
                exit,
                TimeSpan.FromSeconds(10),
                TestContext.CancellationToken).ConfigureAwait(false);

            McpProcessSession mcp = await StartMcpAsync(TestContext.CancellationToken)
                .ConfigureAwait(false);
            await using ConfiguredAsyncDisposable mcpCleanup = mcp.ConfigureAwait(false);
            var progressValues = new ConcurrentQueue<ProgressNotificationValue>();
            var progressReceived = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var progress = new Progress<ProgressNotificationValue>(value =>
            {
                progressValues.Enqueue(value);
                if (value.Progress >= 1)
                {
                    progressReceived.TrySetResult();
                }
            });
            JsonElement opened = await CallAsync(
                mcp.Client,
                "debug_dump_open",
                new Dictionary<string, object?> { ["dumpPath"] = dumpPath },
                TestContext.CancellationToken,
                progress).ConfigureAwait(false);
            await progressReceived.Task.WaitAsync(
                TimeSpan.FromSeconds(10),
                TestContext.CancellationToken).ConfigureAwait(false);
            ProgressNotificationValue[] reportedProgress = [.. progressValues];
            Assert.IsGreaterThanOrEqualTo(2, reportedProgress.Length);
            Assert.Contains(
                (Progress: 0F, Total: 2F),
                reportedProgress.Select(static value =>
                    (Progress: value.Progress, Total: value.Total ?? -1F)));
            Assert.Contains(
                (Progress: 1F, Total: 2F, HasMessage: true),
                reportedProgress.Select(static value =>
                    (
                        Progress: value.Progress,
                        Total: value.Total ?? -1F,
                        HasMessage: value.Message?.Contains(
                            "indexing dump state",
                            StringComparison.Ordinal) == true)));
            string debugSession = opened.GetProperty("debugSession").GetString()
                ?? throw new InvalidDataException("MCP returned no dump-session identifier.");
            Assert.AreEqual("dump", opened.GetProperty("mode").GetString());
            Assert.AreEqual("stopped", opened.GetProperty("state").GetString());
            Assert.AreEqual("dump", opened.GetProperty("stopReason").GetString());
            Assert.AreEqual(1L, opened.GetProperty("stopGeneration").GetInt64());
            Assert.IsFalse(opened.GetProperty("agentControl").GetBoolean());

            JsonElement threads = await CallAsync(
                mcp.Client,
                "debug_threads_get",
                new Dictionary<string, object?>
                {
                    ["debugSession"] = debugSession,
                    ["stopGeneration"] = 1L
                },
                TestContext.CancellationToken).ConfigureAwait(false);
            JsonElement thread = threads.GetProperty("threads")[0];
            Assert.IsGreaterThan(0, thread.GetProperty("id").GetInt32());

            JsonElement stack = await CallAsync(
                mcp.Client,
                "debug_stack_get",
                new Dictionary<string, object?>
                {
                    ["debugSession"] = debugSession,
                    ["stopGeneration"] = 1L,
                    ["threadId"] = thread.GetProperty("id").GetInt32(),
                    ["startFrame"] = 0,
                    ["levels"] = 20
                },
                TestContext.CancellationToken).ConfigureAwait(false);
            Assert.IsGreaterThan(0, stack.GetProperty("totalFrames").GetInt32());

            JsonElement modules = await CallAsync(
                mcp.Client,
                "debug_modules_get",
                new Dictionary<string, object?>
                {
                    ["debugSession"] = debugSession,
                    ["startModule"] = 0,
                    ["moduleCount"] = 100
                },
                TestContext.CancellationToken).ConfigureAwait(false);
            Assert.IsGreaterThan(0, modules.GetProperty("totalModules").GetInt32());
            string moduleNames = string.Join(
                '\n',
                modules.GetProperty("modules").EnumerateArray().Select(
                    static module => module.GetProperty("name").GetString()));
            Assert.Contains(
                "csls-test-process-host",
                moduleNames,
                StringComparison.OrdinalIgnoreCase);

            await AssertToolErrorAsync(
                mcp.Client,
                "debug_agent_control_set",
                new Dictionary<string, object?>
                {
                    ["debugSession"] = debugSession,
                    ["enabled"] = true,
                    ["durationSeconds"] = 60
                },
                "debugger_not_supported",
                TestContext.CancellationToken).ConfigureAwait(false);
            await AssertToolErrorAsync(
                mcp.Client,
                "debug_execution_control",
                new Dictionary<string, object?>
                {
                    ["debugSession"] = debugSession,
                    ["operation"] = "continue",
                    ["stopGeneration"] = 1L
                },
                "debugger_not_supported",
                TestContext.CancellationToken).ConfigureAwait(false);

            JsonElement ended = await CallAsync(
                mcp.Client,
                "debug_session_end",
                new Dictionary<string, object?> { ["debugSession"] = debugSession },
                TestContext.CancellationToken).ConfigureAwait(false);
            Assert.AreEqual("terminated", ended.GetProperty("state").GetString());
            string mcpDiagnostics = await mcp.DisconnectAsync(
                TimeSpan.FromSeconds(20),
                TestContext.CancellationToken).ConfigureAwait(false);
            Assert.DoesNotContain("Unhandled exception", mcpDiagnostics, StringComparison.Ordinal);
        }
        finally
        {
            if (!target.HasExited)
            {
                target.Kill(entireProcessTree: true);
                await target.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            }

            await DirectoryReleaseWaiter.DeleteAsync(
                testDirectory,
                TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Rejects relative or missing dump paths before a worker is started.
    /// </summary>
    [TestMethod]
    [Timeout(30000, CooperativeCancellation = true)]
    public async Task DebuggerDumpSessionRejectsInvalidPaths()
    {
        McpProcessSession mcp = await StartMcpAsync(TestContext.CancellationToken)
            .ConfigureAwait(false);
        await using ConfiguredAsyncDisposable mcpCleanup = mcp.ConfigureAwait(false);
        await AssertToolErrorAsync(
            mcp.Client,
            "debug_dump_open",
            new Dictionary<string, object?> { ["dumpPath"] = "relative.dmp" },
            "debugger_request_invalid",
            TestContext.CancellationToken).ConfigureAwait(false);
    }

    private static Process StartDumpTarget(string repositoryRoot, string finishPath)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = EditorToolResolver.ResolveAbsoluteDotNetHost(),
            RedirectStandardError = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            WorkingDirectory = repositoryRoot
        };
        startInfo.ArgumentList.Add(EditorToolResolver.ResolveTestProcessHost(repositoryRoot));
        startInfo.ArgumentList.Add("--announce-and-spin-until-file");
        startInfo.ArgumentList.Add(finishPath);
        return Process.Start(startInfo)
            ?? throw new InvalidOperationException("The dump test target did not start.");
    }

    private static async Task WaitForReadyAsync(
        Process target,
        CancellationToken cancellationToken)
    {
        char[] ready = new char[5];
        int totalRead = 0;
        while (totalRead < ready.Length)
        {
            int read = await target.StandardOutput.ReadAsync(
                ready.AsMemory(totalRead),
                cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                string diagnostics = await target.StandardError.ReadToEndAsync(cancellationToken)
                    .ConfigureAwait(false);
                throw new InvalidDataException(
                    $"The dump target exited before readiness: {diagnostics}");
            }

            totalRead += read;
        }

        Assert.AreEqual("ready", new string(ready));
    }
}
