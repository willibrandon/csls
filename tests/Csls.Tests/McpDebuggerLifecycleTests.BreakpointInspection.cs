using ModelContextProtocol.Client;
using System.Text.Json;

namespace Csls.Tests;

/// <summary>
/// Verifies authoritative MCP breakpoint inspection against a real stopped target.
/// </summary>
public sealed partial class McpDebuggerLifecycleTests
{
    private static async Task AssertBreakpointInspectionAsync(
        McpClient client,
        string debugSession,
        long generation,
        string sourcePath,
        CancellationToken cancellationToken)
    {
        JsonElement tool = await CallAsync(
            client,
            "debug_breakpoints_get",
            new Dictionary<string, object?> { ["debugSession"] = debugSession },
            cancellationToken).ConfigureAwait(false);
        AssertBreakpointSnapshot(tool, debugSession, generation, sourcePath);

        JsonElement resource = await ReadAsync(
            client,
            $"csls://debug/breakpoints/{debugSession}",
            cancellationToken).ConfigureAwait(false);
        AssertBreakpointSnapshot(resource, debugSession, generation, sourcePath);
    }

    private static void AssertBreakpointSnapshot(
        JsonElement snapshot,
        string debugSession,
        long generation,
        string sourcePath)
    {
        Assert.AreEqual(debugSession, snapshot.GetProperty("debugSession").GetString());
        Assert.AreEqual("stopped", snapshot.GetProperty("state").GetString());
        Assert.AreEqual(generation, snapshot.GetProperty("stopGeneration").GetInt64());

        JsonElement source = Assert.ContainsSingle(
            snapshot.GetProperty("sourceBreakpoints").EnumerateArray());
        Assert.AreEqual(sourcePath, source.GetProperty("sourcePath").GetString());
        Assert.AreEqual(">=1", source.GetProperty("hitCondition").GetString());

        JsonElement function = Assert.ContainsSingle(
            snapshot.GetProperty("functionBreakpoints").EnumerateArray());
        Assert.AreEqual(
            "Csls.TestProcessHost.DebuggerFixture.WaitForSignal",
            function.GetProperty("name").GetString());
        Assert.AreEqual("%2", function.GetProperty("hitCondition").GetString());

        JsonElement instruction = Assert.ContainsSingle(
            snapshot.GetProperty("instructionBreakpoints").EnumerateArray());
        Assert.AreEqual("1", instruction.GetProperty("hitCondition").GetString());

        JsonElement exception = Assert.ContainsSingle(
            snapshot.GetProperty("exceptionBreakpoints").EnumerateArray());
        Assert.AreEqual("thrown", exception.GetProperty("breakMode").GetString());
        Assert.AreEqual(
            "System.InvalidOperationException",
            Assert.ContainsSingle(exception.GetProperty("exceptionTypeNames").EnumerateArray())
                .GetString());
    }
}
