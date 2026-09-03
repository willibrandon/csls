using ModelContextProtocol.Server;
using System.ComponentModel;

namespace Csls.Mcp.Worker;

/// <summary>
/// Exposes runtime-approved instruction-pointer movement.
/// </summary>
internal sealed partial class CslsMcpDebuggerExecutionTools
{
    /// <summary>
    /// Moves one managed thread to a target returned for the exact stop generation.
    /// </summary>
    [McpServerTool(
        Name = "debug_goto",
        Title = "Move .NET instruction pointer",
        Destructive = true,
        Idempotent = false,
        OpenWorld = true,
        ReadOnly = false,
        UseStructuredContent = true,
        OutputSchemaType = typeof(McpDebugSessionInfo))]
    [Description("Move a managed thread to a runtime-approved debug_goto_targets_get destination. Requires agentControl and the exact stopGeneration.")]
    public Task<ModelContextProtocol.Protocol.CallToolResult> GotoAsync(
        [Description("Opaque identifier returned by a debugger lifecycle tool.")]
        string debugSession,
        [Description("Exact current positive stop generation.")]
        long stopGeneration,
        [Description("Positive managed thread identifier.")]
        int threadId,
        [Description("Positive target returned by debug_goto_targets_get.")]
        int targetId,
        CancellationToken cancellationToken) =>
        McpDebuggerToolResult.RunAsync(() => _broker.GotoAsync(
            debugSession,
            stopGeneration,
            threadId,
            targetId,
            cancellationToken));
}
