using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Csls.Tests;

/// <summary>
/// Drives real MCP debugger lifecycle operations and assertions.
/// </summary>
public sealed partial class McpDebuggerLifecycleTests
{
    private static async Task ExerciseLifecycleAsync(
        string repositoryRoot,
        string sourcePath,
        int breakpointLine,
        int localLine,
        string testDirectory,
        CancellationToken cancellationToken)
    {
        McpProcessSession mcp = await StartMcpAsync(cancellationToken).ConfigureAwait(false);
        await using ConfiguredAsyncDisposable mcpCleanup = mcp.ConfigureAwait(false);
        JsonElement started = await StartTargetAsync(
            mcp.Client,
            repositoryRoot,
            sourcePath,
            breakpointLine,
            Path.Join(testDirectory, "first.signal"),
            agentControl: false,
            cancellationToken).ConfigureAwait(false);
        string debugSession = started.GetProperty("debugSession").GetString()
            ?? throw new InvalidDataException("MCP returned no debugger-session identifier.");
        int processId = started.GetProperty("processId").GetInt32();
        ProcessExitObservation exit = ProcessExitWaiter.Observe(processId);

        JsonElement stopped = await WaitForStoppedAsync(
            mcp.Client,
            debugSession,
            cancellationToken).ConfigureAwait(false);
        Assert.AreEqual("breakpoint", stopped.GetProperty("stopReason").GetString());
        Assert.IsGreaterThan(0, stopped.GetProperty("stopGeneration").GetInt64());
        Assert.IsFalse(stopped.GetProperty("agentControl").GetBoolean());
        await AssertInspectionAndControlDenialAsync(
            mcp.Client,
            stopped,
            sourcePath,
            localLine,
            EditorToolResolver.ResolveTestProcessHost(repositoryRoot),
            cancellationToken).ConfigureAwait(false);

        JsonElement listed = await CallAsync(
            mcp.Client,
            "debug_sessions_list",
            [],
            cancellationToken).ConfigureAwait(false);
        Assert.AreEqual(debugSession, listed[0].GetProperty("debugSession").GetString());
        JsonElement ended = await CallAsync(
            mcp.Client,
            "debug_session_end",
            new Dictionary<string, object?> { ["debugSession"] = debugSession },
            cancellationToken).ConfigureAwait(false);
        Assert.AreEqual("terminated", ended.GetProperty("state").GetString());
        await ProcessExitWaiter.WaitAsync(exit, TimeSpan.FromSeconds(10), cancellationToken)
            .ConfigureAwait(false);

        JsonElement second = await StartTargetAsync(
            mcp.Client,
            repositoryRoot,
            sourcePath,
            breakpointLine,
            Path.Join(testDirectory, "second.signal"),
            agentControl: true,
            cancellationToken).ConfigureAwait(false);
        int secondProcessId = second.GetProperty("processId").GetInt32();
        ProcessExitObservation secondExit = ProcessExitWaiter.Observe(secondProcessId);
        JsonElement secondStopped = await WaitForStoppedAsync(
            mcp.Client,
            second.GetProperty("debugSession").GetString()!,
            cancellationToken).ConfigureAwait(false);
        await AssertControlledBreakpointUpdatesAsync(
            mcp.Client,
            secondStopped,
            sourcePath,
            cancellationToken).ConfigureAwait(false);
        JsonElement continued = await CallAsync(
            mcp.Client,
            "debug_execution_control",
            new Dictionary<string, object?>
            {
                ["debugSession"] = secondStopped.GetProperty("debugSession").GetString(),
                ["operation"] = "continue",
                ["stopGeneration"] = secondStopped.GetProperty("stopGeneration").GetInt64()
            },
            cancellationToken).ConfigureAwait(false);
        Assert.AreEqual("running", continued.GetProperty("state").GetString());
        string diagnostics = await mcp.DisconnectAsync(
            TimeSpan.FromSeconds(20),
            cancellationToken).ConfigureAwait(false);
        Assert.DoesNotContain("fail:", diagnostics, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            "Unhandled exception",
            diagnostics,
            StringComparison.OrdinalIgnoreCase);
        await ProcessExitWaiter.WaitAsync(secondExit, TimeSpan.FromSeconds(10), cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task<JsonElement> StartTargetAsync(
        McpClient client,
        string repositoryRoot,
        string sourcePath,
        int breakpointLine,
        string signalPath,
        bool agentControl,
        CancellationToken cancellationToken) =>
        await CallAsync(
            client,
            "debug_session_start",
            new Dictionary<string, object?>
            {
                ["program"] = EditorToolResolver.ResolveTestProcessHost(repositoryRoot),
                ["workingDirectory"] = repositoryRoot,
                ["arguments"] = new[] { "--debugger-fixture", signalPath },
                ["initialSourcePath"] = sourcePath,
                ["initialLine"] = breakpointLine,
                ["agentControl"] = agentControl
            },
            cancellationToken).ConfigureAwait(false);

    private static async Task<JsonElement> WaitForStoppedAsync(
        McpClient client,
        string debugSession,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            JsonElement state = await CallAsync(
                client,
                "debug_session_get",
                new Dictionary<string, object?> { ["debugSession"] = debugSession },
                cancellationToken).ConfigureAwait(false);
            if (state.GetProperty("state").GetString() == "stopped")
            {
                return state;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(20), cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private static async Task<JsonElement> CallAsync(
        McpClient client,
        string tool,
        Dictionary<string, object?> arguments,
        CancellationToken cancellationToken)
    {
        CallToolResult result = await client.CallToolAsync(
            tool,
            arguments,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        Assert.IsNull(result.IsError, tool);
        Assert.IsTrue(result.StructuredContent.HasValue, tool);
        JsonElement structuredContent = result.StructuredContent.Value;
        if (structuredContent.ValueKind == JsonValueKind.Object &&
            structuredContent.TryGetProperty("debugSession", out JsonElement debugSession))
        {
            ResourceLinkBlock link = Assert.ContainsSingle(
                result.Content.OfType<ResourceLinkBlock>());
            Assert.AreEqual(
                $"csls://debug/session/{debugSession.GetString()}",
                link.Uri,
                tool);
            Assert.AreEqual("application/json", link.MimeType, tool);
        }

        return structuredContent;
    }

    private static void AssertAnnotations(
        IList<McpClientTool> tools,
        string name,
        bool readOnly,
        bool destructive,
        bool idempotent,
        bool openWorld)
    {
        ToolAnnotations annotations = tools.Single(tool => tool.Name == name)
            .ProtocolTool.Annotations!;
        Assert.AreEqual(readOnly, annotations.ReadOnlyHint, name);
        Assert.AreEqual(destructive, annotations.DestructiveHint, name);
        Assert.AreEqual(idempotent, annotations.IdempotentHint, name);
        Assert.AreEqual(openWorld, annotations.OpenWorldHint, name);
    }
}
