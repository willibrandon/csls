using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Csls.Tests;

/// <summary>
/// Verifies debugger target restart through real MCP and worker processes.
/// </summary>
public sealed partial class McpDebuggerLifecycleTests
{
    /// <summary>
    /// Replaces a stopped target while preserving session identity and breakpoints.
    /// </summary>
    [TestMethod]
    [Timeout(60000, CooperativeCancellation = true)]
    [OSCondition(ConditionMode.Exclude, OperatingSystems.Windows)]
    public async Task McpRestartPreservesSessionAndBreakpointPolicy()
    {
        string repositoryRoot = EditorToolResolver.FindRepositoryRoot();
        string sourcePath = Path.Join(
            repositoryRoot,
            "tests",
            "Csls.TestProcessHost",
            "DebuggerFixture.cs");
        int breakpointLine = (await File.ReadAllLinesAsync(
            sourcePath,
            TestContext.CancellationToken).ConfigureAwait(false))
            .Select(static (line, index) => (Line: line, Number: index + 1))
            .Single(static candidate => candidate.Line.Contains(
                "Thread.SpinWait(10_000);",
                StringComparison.Ordinal))
            .Number;
        string testDirectory = Path.Join(
            Path.GetTempPath(),
            $"csls-mcp-debugger-restart-{Guid.NewGuid():N}");
        Directory.CreateDirectory(testDirectory);
        try
        {
            await ExerciseRestartAsync(
                repositoryRoot,
                sourcePath,
                breakpointLine,
                testDirectory,
                TestContext.CancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (Directory.Exists(testDirectory))
            {
                Directory.Delete(testDirectory, recursive: true);
            }
        }
    }

    private static async Task ExerciseRestartAsync(
        string repositoryRoot,
        string sourcePath,
        int breakpointLine,
        string testDirectory,
        CancellationToken cancellationToken)
    {
        McpProcessSession mcp = await StartMcpAsync(cancellationToken).ConfigureAwait(false);
        await using ConfiguredAsyncDisposable cleanup = mcp.ConfigureAwait(false);
        JsonElement started = await StartTargetAsync(
            mcp.Client,
            repositoryRoot,
            sourcePath,
            breakpointLine,
            Path.Join(testDirectory, "continue.signal"),
            agentControl: true,
            cancellationToken).ConfigureAwait(false);
        string debugSession = started.GetProperty("debugSession").GetString()!;
        int originalProcessId = started.GetProperty("processId").GetInt32();
        ProcessExitObservation originalExit = ProcessExitWaiter.Observe(originalProcessId);
        JsonElement stopped = await WaitForStoppedAsync(
            mcp.Client,
            debugSession,
            cancellationToken).ConfigureAwait(false);
        long originalGeneration = stopped.GetProperty("stopGeneration").GetInt64();
        await AssertToolErrorAsync(
            mcp.Client,
            "debug_session_restart",
            new Dictionary<string, object?>
            {
                ["debugSession"] = debugSession,
                ["stopGeneration"] = originalGeneration + 1
            },
            "debugger_stale_generation",
            cancellationToken).ConfigureAwait(false);

        JsonElement restarted = await AssertResourceSubscriptionAsync(
            mcp.Client,
            $"csls://debug/session/{debugSession}",
            () => CallAsync(
                mcp.Client,
                "debug_session_restart",
                new Dictionary<string, object?>
                {
                    ["debugSession"] = debugSession,
                    ["stopGeneration"] = originalGeneration
                },
                cancellationToken),
            cancellationToken).ConfigureAwait(false);
        Assert.AreEqual(debugSession, restarted.GetProperty("debugSession").GetString());
        int replacementProcessId = restarted.GetProperty("processId").GetInt32();
        Assert.AreNotEqual(originalProcessId, replacementProcessId);
        await ProcessExitWaiter.WaitAsync(
            originalExit,
            TimeSpan.FromSeconds(10),
            cancellationToken).ConfigureAwait(false);
        ProcessExitObservation replacementExit = ProcessExitWaiter.Observe(replacementProcessId);

        JsonElement replacementStop = await WaitForStoppedAsync(
            mcp.Client,
            debugSession,
            cancellationToken).ConfigureAwait(false);
        long replacementGeneration = replacementStop.GetProperty("stopGeneration").GetInt64();
        Assert.IsGreaterThan(originalGeneration, replacementGeneration);
        JsonElement breakpoints = await CallAsync(
            mcp.Client,
            "debug_breakpoints_get",
            new Dictionary<string, object?> { ["debugSession"] = debugSession },
            cancellationToken).ConfigureAwait(false);
        JsonElement sourceBreakpoint = Assert.ContainsSingle(
            breakpoints.GetProperty("sourceBreakpoints").EnumerateArray());
        Assert.AreEqual(sourcePath, sourceBreakpoint.GetProperty("sourcePath").GetString());
        Assert.IsTrue(sourceBreakpoint.GetProperty("verified").GetBoolean());

        _ = await CallAsync(
            mcp.Client,
            "debug_execution_control",
            new Dictionary<string, object?>
            {
                ["debugSession"] = debugSession,
                ["operation"] = "continue",
                ["stopGeneration"] = replacementGeneration
            },
            cancellationToken).ConfigureAwait(false);
        string diagnostics = await mcp.DisconnectAsync(
            TimeSpan.FromSeconds(20),
            cancellationToken).ConfigureAwait(false);
        Assert.DoesNotContain("fail:", diagnostics, StringComparison.OrdinalIgnoreCase);
        await ProcessExitWaiter.WaitAsync(
            replacementExit,
            TimeSpan.FromSeconds(10),
            cancellationToken).ConfigureAwait(false);
    }
}
