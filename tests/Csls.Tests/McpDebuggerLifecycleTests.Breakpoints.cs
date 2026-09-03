using ModelContextProtocol.Client;
using System.Text.Json;

namespace Csls.Tests;

/// <summary>
/// Verifies authorized MCP breakpoint replacement against a real stopped target.
/// </summary>
public sealed partial class McpDebuggerLifecycleTests
{
    private static readonly string[] s_invalidOperationExceptionType =
        ["System.InvalidOperationException"];

    private static async Task AssertControlledBreakpointUpdatesAsync(
        McpClient client,
        JsonElement stopped,
        string sourcePath,
        CancellationToken cancellationToken)
    {
        string debugSession = stopped.GetProperty("debugSession").GetString()!;
        long generation = stopped.GetProperty("stopGeneration").GetInt64();
        int threadId = stopped.GetProperty("stoppedThreadId").GetInt32();
        JsonElement stack = await CallAsync(
            client,
            "debug_stack_get",
            new Dictionary<string, object?>
            {
                ["debugSession"] = debugSession,
                ["stopGeneration"] = generation,
                ["threadId"] = threadId,
                ["levels"] = 64
            },
            cancellationToken).ConfigureAwait(false);
        JsonElement frame = stack.GetProperty("stackFrames").EnumerateArray().Single(item =>
            item.TryGetProperty("source", out JsonElement source) &&
            source.TryGetProperty("path", out JsonElement path) &&
            string.Equals(path.GetString(), sourcePath, StringComparison.Ordinal));

        JsonElement sourceBreakpoints = await CallAsync(
            client,
            "debug_source_breakpoints_set",
            new Dictionary<string, object?>
            {
                ["debugSession"] = debugSession,
                ["stopGeneration"] = generation,
                ["sourcePath"] = sourcePath,
                ["breakpoints"] = new[]
                {
                    new Dictionary<string, object?> { ["line"] = frame.GetProperty("line").GetInt32() }
                }
            },
            cancellationToken).ConfigureAwait(false);
        Assert.IsTrue(sourceBreakpoints.GetProperty("breakpoints")[0]
            .GetProperty("verified").GetBoolean());

        JsonElement functions = await CallAsync(
            client,
            "debug_function_breakpoints_set",
            new Dictionary<string, object?>
            {
                ["debugSession"] = debugSession,
                ["stopGeneration"] = generation,
                ["breakpoints"] = new[]
                {
                    new Dictionary<string, object?>
                    {
                        ["name"] = "Csls.TestProcessHost.DebuggerFixture.WaitForSignal"
                    }
                }
            },
            cancellationToken).ConfigureAwait(false);
        Assert.IsTrue(functions.GetProperty("breakpoints")[0]
            .GetProperty("verified").GetBoolean());

        JsonElement instructions = await CallAsync(
            client,
            "debug_instruction_breakpoints_set",
            new Dictionary<string, object?>
            {
                ["debugSession"] = debugSession,
                ["stopGeneration"] = generation,
                ["breakpoints"] = new[]
                {
                    new Dictionary<string, object?>
                    {
                        ["instructionReference"] = frame
                            .GetProperty("instructionReference").GetString()
                    }
                }
            },
            cancellationToken).ConfigureAwait(false);
        Assert.IsTrue(instructions.GetProperty("breakpoints")[0]
            .GetProperty("verified").GetBoolean());

        JsonElement exceptions = await CallAsync(
            client,
            "debug_exception_breakpoints_set",
            new Dictionary<string, object?>
            {
                ["debugSession"] = debugSession,
                ["stopGeneration"] = generation,
                ["breakpoints"] = new[]
                {
                    new Dictionary<string, object?>
                    {
                        ["breakMode"] = "thrown",
                        ["exceptionTypeNames"] = s_invalidOperationExceptionType
                    }
                }
            },
            cancellationToken).ConfigureAwait(false);
        Assert.AreEqual(
            "thrown",
            exceptions.GetProperty("breakpoints")[0].GetProperty("breakMode").GetString());
    }
}
