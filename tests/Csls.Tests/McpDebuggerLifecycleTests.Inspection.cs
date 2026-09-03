using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using System.Text.Json;

namespace Csls.Tests;

/// <summary>
/// Verifies generation-aware MCP debugger inspection and control authorization.
/// </summary>
public sealed partial class McpDebuggerLifecycleTests
{
    private static async Task AssertInspectionAndControlDenialAsync(
        McpClient client,
        JsonElement stopped,
        string sourcePath,
        int gotoLine,
        string programPath,
        CancellationToken cancellationToken)
    {
        string debugSession = stopped.GetProperty("debugSession").GetString()!;
        long generation = stopped.GetProperty("stopGeneration").GetInt64();
        int stoppedThreadId = stopped.GetProperty("stoppedThreadId").GetInt32();
        JsonElement threads = await CallAsync(
            client,
            "debug_threads_get",
            new Dictionary<string, object?>
            {
                ["debugSession"] = debugSession,
                ["stopGeneration"] = generation
            },
            cancellationToken).ConfigureAwait(false);
        Assert.AreEqual(generation, threads.GetProperty("stopGeneration").GetInt64());
        Assert.Contains(
            stoppedThreadId,
            threads.GetProperty("threads").EnumerateArray()
                .Select(static thread => thread.GetProperty("id").GetInt32()));

        JsonElement stack = await CallAsync(
            client,
            "debug_stack_get",
            new Dictionary<string, object?>
            {
                ["debugSession"] = debugSession,
                ["stopGeneration"] = generation,
                ["threadId"] = stoppedThreadId,
                ["levels"] = 64
            },
            cancellationToken).ConfigureAwait(false);
        JsonElement frame = stack.GetProperty("stackFrames").EnumerateArray().Single(item =>
            item.TryGetProperty("source", out JsonElement source) &&
            source.TryGetProperty("path", out JsonElement path) &&
            string.Equals(path.GetString(), sourcePath, StringComparison.Ordinal));
        Assert.IsGreaterThan(0, stack.GetProperty("totalFrames").GetInt32());

        JsonElement scopes = await CallAsync(
            client,
            "debug_scopes_get",
            new Dictionary<string, object?>
            {
                ["debugSession"] = debugSession,
                ["stopGeneration"] = generation,
                ["frameId"] = frame.GetProperty("id").GetInt32()
            },
            cancellationToken).ConfigureAwait(false);
        JsonElement locals = scopes.GetProperty("scopes").EnumerateArray().Single(item =>
            item.GetProperty("name").GetString() == "Locals");
        JsonElement variables = await CallAsync(
            client,
            "debug_variables_get",
            new Dictionary<string, object?>
            {
                ["debugSession"] = debugSession,
                ["stopGeneration"] = generation,
                ["variablesReference"] = locals.GetProperty("variablesReference").GetInt32()
            },
            cancellationToken).ConfigureAwait(false);
        JsonElement localNumber = variables.GetProperty("variables").EnumerateArray().Single(
            item => item.GetProperty("name").GetString() == "localNumber");
        Assert.AreEqual("43", localNumber.GetProperty("value").GetString());

        JsonElement modules = await CallAsync(
            client,
            "debug_modules_get",
            new Dictionary<string, object?> { ["debugSession"] = debugSession },
            cancellationToken).ConfigureAwait(false);
        JsonElement programModule = modules.GetProperty("modules").EnumerateArray()
            .Single(module => string.Equals(
                module.GetProperty("path").GetString(),
                programPath,
                StringComparison.Ordinal));
        Assert.AreEqual(
            "portablePdb",
            programModule.GetProperty("symbolKind").GetString());

        await AssertOutputAsync(
            client,
            debugSession,
            generation,
            cancellationToken).ConfigureAwait(false);

        await AssertAdvancedInspectionAsync(
            client,
            debugSession,
            generation,
            stoppedThreadId,
            frame,
            variables,
            sourcePath,
            gotoLine,
            cancellationToken).ConfigureAwait(false);

        await AssertToolErrorAsync(
            client,
            "debug_threads_get",
            new Dictionary<string, object?>
            {
                ["debugSession"] = debugSession,
                ["stopGeneration"] = generation + 1
            },
            "debugger_stale_generation",
            cancellationToken).ConfigureAwait(false);
        await AssertToolErrorAsync(
            client,
            "debug_execution_control",
            new Dictionary<string, object?>
            {
                ["debugSession"] = debugSession,
                ["operation"] = "continue",
                ["stopGeneration"] = generation
            },
            "debugger_control_denied",
            cancellationToken).ConfigureAwait(false);
        await AssertToolErrorAsync(
            client,
            "debug_source_breakpoints_set",
            new Dictionary<string, object?>
            {
                ["debugSession"] = debugSession,
                ["stopGeneration"] = generation,
                ["sourcePath"] = sourcePath,
                ["breakpoints"] = Array.Empty<object>()
            },
            "debugger_control_denied",
            cancellationToken).ConfigureAwait(false);
    }

    private static async Task AssertToolErrorAsync(
        McpClient client,
        string tool,
        Dictionary<string, object?> arguments,
        string expectedCode,
        CancellationToken cancellationToken)
    {
        CallToolResult result = await client.CallToolAsync(
            tool,
            arguments,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        Assert.IsTrue(result.IsError, tool);
        Assert.IsNull(result.StructuredContent, tool);
        Assert.IsNotNull(result.Meta, tool);
        string? actualCode = result.Meta["errorCode"]?.GetValue<string>();
        Assert.IsNotNull(actualCode, tool);
        Assert.AreEqual(expectedCode, actualCode, tool);
        Assert.Contains(
            expectedCode,
            result.Content.OfType<TextContentBlock>().Single().Text,
            StringComparison.Ordinal);
    }
}
