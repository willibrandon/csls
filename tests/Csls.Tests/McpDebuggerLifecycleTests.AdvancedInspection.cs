using ModelContextProtocol.Client;
using System.Text.Json;

namespace Csls.Tests;

/// <summary>
/// Verifies bounded advanced MCP debugger reads against a real stopped target.
/// </summary>
public sealed partial class McpDebuggerLifecycleTests
{
    private static async Task AssertAdvancedInspectionAsync(
        McpClient client,
        string debugSession,
        long generation,
        int threadId,
        JsonElement frame,
        JsonElement variables,
        string sourcePath,
        int gotoLine,
        CancellationToken cancellationToken)
    {
        JsonElement localArray = variables.GetProperty("variables").EnumerateArray().Single(
            item => item.GetProperty("name").GetString() == "localArray");
        string memoryReference = localArray.GetProperty("memoryReference").GetString()!;
        JsonElement memory = await CallAsync(
            client,
            "debug_memory_read",
            new Dictionary<string, object?>
            {
                ["debugSession"] = debugSession,
                ["stopGeneration"] = generation,
                ["memoryReference"] = memoryReference,
                ["count"] = 12
            },
            cancellationToken).ConfigureAwait(false);
        byte[] bytes = Convert.FromBase64String(memory.GetProperty("data").GetString()!);
        Assert.HasCount(12, bytes);
        Assert.AreEqual(0, memory.GetProperty("unreadableBytes").GetInt32());
        JsonElement memoryResource = await ReadAsync(
            client,
            $"csls://debug/memory/{debugSession}/{generation}?memoryReference=" +
                $"{Uri.EscapeDataString(memoryReference)}&count=12",
            cancellationToken).ConfigureAwait(false);
        Assert.HasCount(
            12,
            Convert.FromBase64String(memoryResource.GetProperty("data").GetString()!));

        string instructionReference = frame.GetProperty("instructionReference")
            .GetString()!;
        JsonElement disassembly = await CallAsync(
            client,
            "debug_disassemble",
            new Dictionary<string, object?>
            {
                ["debugSession"] = debugSession,
                ["stopGeneration"] = generation,
                ["instructionReference"] = instructionReference,
                ["instructionCount"] = 8
            },
            cancellationToken).ConfigureAwait(false);
        Assert.HasCount(8, disassembly.GetProperty("instructions").EnumerateArray());
        JsonElement disassemblyResource = await ReadAsync(
            client,
            $"csls://debug/disassembly/{debugSession}/{generation}?instructionReference=" +
                $"{Uri.EscapeDataString(instructionReference)}&instructionCount=8",
            cancellationToken).ConfigureAwait(false);
        Assert.HasCount(
            8,
            disassemblyResource.GetProperty("instructions").EnumerateArray());

        JsonElement stepTargets = await CallAsync(
            client,
            "debug_step_targets_get",
            new Dictionary<string, object?>
            {
                ["debugSession"] = debugSession,
                ["stopGeneration"] = generation,
                ["frameId"] = frame.GetProperty("id").GetInt32()
            },
            cancellationToken).ConfigureAwait(false);
        Assert.AreEqual(
            generation,
            stepTargets.GetProperty("stopGeneration").GetInt64());
        Assert.AreEqual(
            debugSession,
            stepTargets.GetProperty("debugSession").GetString());
        Assert.IsLessThanOrEqualTo(
            256,
            stepTargets.GetProperty("targets").GetArrayLength());

        JsonElement gotoTargets = await CallAsync(
            client,
            "debug_goto_targets_get",
            new Dictionary<string, object?>
            {
                ["debugSession"] = debugSession,
                ["stopGeneration"] = generation,
                ["frameId"] = frame.GetProperty("id").GetInt32(),
                ["sourcePath"] = sourcePath,
                ["line"] = gotoLine
            },
            cancellationToken).ConfigureAwait(false);
        Assert.IsNotEmpty(gotoTargets.GetProperty("targets").EnumerateArray());
        int gotoTargetId = gotoTargets.GetProperty("targets")[0]
            .GetProperty("id").GetInt32();
        await AssertToolErrorAsync(
            client,
            "debug_goto",
            new Dictionary<string, object?>
            {
                ["debugSession"] = debugSession,
                ["stopGeneration"] = generation,
                ["threadId"] = threadId,
                ["targetId"] = gotoTargetId
            },
            "debugger_control_denied",
            cancellationToken).ConfigureAwait(false);

        await AssertToolErrorAsync(
            client,
            "debug_exception_get",
            new Dictionary<string, object?>
            {
                ["debugSession"] = debugSession,
                ["stopGeneration"] = generation,
                ["threadId"] = threadId
            },
            "debugger_operation_failed",
            cancellationToken).ConfigureAwait(false);
    }
}
