using ModelContextProtocol.Protocol;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Csls.Tests;

/// <summary>
/// Verifies MCP session capacity and process ownership after a real debugger worker fails.
/// </summary>
public sealed partial class McpDebuggerLifecycleTests
{
    /// <summary>
    /// Releases a failed worker's session slot so the connection can use its complete bounded capacity again.
    /// </summary>
    [TestMethod]
    [OSCondition(OperatingSystems.Linux)]
    [Timeout(60000, CooperativeCancellation = true)]
    public async Task FailedWorkerEndReleasesSessionCapacity()
    {
        string repository = EditorToolResolver.FindRepositoryRoot();
        McpProcessSession mcp = await StartMcpAsync(TestContext.CancellationToken).ConfigureAwait(false);
        await using ConfiguredAsyncDisposable cleanup = mcp.ConfigureAwait(false);
        var arguments = new Dictionary<string, object?>
        {
            ["program"] = EditorToolResolver.ResolveTestProcessHost(repository),
            ["workingDirectory"] = repository,
            ["stopAtEntry"] = true
        };
        JsonElement started = await CallAsync(mcp.Client, "debug_session_start", arguments, TestContext.CancellationToken)
            .ConfigureAwait(false);
        string failedSession = started.GetProperty("debugSession").GetString()!;
        using var target = Process.GetProcessById(started.GetProperty("processId").GetInt32());
        try
        {
            _ = await WaitForStoppedAsync(mcp.Client, failedSession, TestContext.CancellationToken).ConfigureAwait(false);
            using Process worker = await FindOwnedDebuggerWorkerAsync(target.Id, mcp.LauncherProcess.Id,
                EditorToolResolver.ResolveDebuggerWorker(repository), TestContext.CancellationToken).ConfigureAwait(false);
            worker.Kill();
            await worker.WaitForExitAsync(TestContext.CancellationToken).ConfigureAwait(false);
            Assert.IsTrue(worker.HasExited);
            CallToolResult ended = await mcp.Client.CallToolAsync("debug_session_end",
                new Dictionary<string, object?> { ["debugSession"] = failedSession },
                cancellationToken: TestContext.CancellationToken).ConfigureAwait(false);
            Assert.IsTrue(ended.IsError);
            JsonElement empty = await CallAsync(mcp.Client, "debug_sessions_list", [], TestContext.CancellationToken)
                .ConfigureAwait(false);
            Assert.IsEmpty(empty.GetProperty("sessions").EnumerateArray());

            var sessions = new List<(string Id, ProcessExitObservation Exit)>();
            for (int index = 0; index < 8; index++)
            {
                JsonElement next = await CallAsync(mcp.Client, "debug_session_start", arguments, TestContext.CancellationToken)
                    .ConfigureAwait(false);
                string id = next.GetProperty("debugSession").GetString()!;
                sessions.Add((id, ProcessExitWaiter.Observe(next.GetProperty("processId").GetInt32())));
                JsonElement stopped = await WaitForStoppedAsync(mcp.Client, id, TestContext.CancellationToken).ConfigureAwait(false);
                Assert.AreEqual("entry", stopped.GetProperty("stopReason").GetString());
            }
            JsonElement full = await CallAsync(mcp.Client, "debug_sessions_list", [], TestContext.CancellationToken)
                .ConfigureAwait(false);
            Assert.AreSequenceEqual(sessions.Select(static session => session.Id).Order(StringComparer.Ordinal),
                full.GetProperty("sessions").EnumerateArray().Select(static session => session.GetProperty("debugSession").GetString()));
            await AssertToolErrorAsync(mcp.Client, "debug_session_start", arguments, "debugger_session_limit",
                TestContext.CancellationToken).ConfigureAwait(false);
            foreach ((string id, ProcessExitObservation exit) in sessions)
            {
                JsonElement terminal = await CallAsync(mcp.Client, "debug_session_end",
                    new Dictionary<string, object?> { ["debugSession"] = id }, TestContext.CancellationToken).ConfigureAwait(false);
                Assert.AreEqual("terminated", terminal.GetProperty("state").GetString());
                await ProcessExitWaiter.WaitAsync(exit, TimeSpan.FromSeconds(10), TestContext.CancellationToken).ConfigureAwait(false);
            }
            JsonElement cleared = await CallAsync(mcp.Client, "debug_sessions_list", [], TestContext.CancellationToken)
                .ConfigureAwait(false);
            Assert.IsEmpty(cleared.GetProperty("sessions").EnumerateArray());
        }
        finally
        {
            if (!target.HasExited)
            {
                target.Kill();
            }
            await target.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
        }
    }

    private static async Task<Process> FindOwnedDebuggerWorkerAsync(int targetId, int launcherId, string workerPath,
        CancellationToken cancellationToken)
    {
        int current = targetId;
        int? workerId = null;
        for (int depth = 0; depth < 16 && current != launcherId; depth++)
        {
            string[] status = await File.ReadAllLinesAsync($"/proc/{current}/status", cancellationToken).ConfigureAwait(false);
            current = int.Parse(status.Single(static line => line.StartsWith("PPid:", StringComparison.Ordinal))[5..].Trim(),
                CultureInfo.InvariantCulture);
            Assert.IsGreaterThan(1, current, "The selected worker must remain a descendant of the owned MCP launcher.");
            string[] command = (await File.ReadAllTextAsync($"/proc/{current}/cmdline", cancellationToken).ConfigureAwait(false))
                .Split('\0', StringSplitOptions.RemoveEmptyEntries);
            if (command.Contains(workerPath, StringComparer.Ordinal) && command.Contains("control", StringComparer.Ordinal))
            {
                Assert.IsNull(workerId, "The target must identify exactly one debugger worker in its owned ancestor chain.");
                workerId = current;
            }
        }
        Assert.AreEqual(launcherId, current, "The worker must belong to this test's MCP launcher.");
        int ownedWorkerId = workerId ?? throw new AssertFailedException(
            "The target's ancestor chain must contain the selected debugger worker.");
        return Process.GetProcessById(ownedWorkerId);
    }
}
