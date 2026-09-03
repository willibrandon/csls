using ModelContextProtocol.Server;
using System.ComponentModel;

namespace Csls.Mcp.Worker;

/// <summary>
/// Exposes deterministic managed-IL and exception breakpoint replacement.
/// </summary>
internal sealed partial class CslsMcpDebuggerBreakpointTools
{
    /// <summary>
    /// Replaces every managed-IL instruction breakpoint.
    /// </summary>
    [McpServerTool(
        Name = "debug_instruction_breakpoints_set",
        Title = "Set .NET instruction breakpoints",
        Destructive = true,
        Idempotent = true,
        OpenWorld = true,
        ReadOnly = false,
        UseStructuredContent = true,
        OutputSchemaType = typeof(McpDebugInstructionBreakpointsResult))]
    [Description("Replace all managed-IL instruction breakpoints at an exact stopped generation. Requires an active debug_agent_control_set grant.")]
    public Task<ModelContextProtocol.Protocol.CallToolResult> SetInstructionBreakpointsAsync(
        [Description("Opaque identifier returned by a debugger lifecycle tool.")]
        string debugSession,
        [Description("Exact current positive stop generation.")]
        long stopGeneration,
        [Description("Complete replacement list with optional conditions and hit counts; an empty list clears every instruction breakpoint.")]
        IReadOnlyList<McpDebugInstructionBreakpoint> breakpoints,
        CancellationToken cancellationToken) =>
        McpDebuggerToolResult.RunAsync(() => _broker.SetInstructionBreakpointsAsync(
            debugSession,
            stopGeneration,
            breakpoints,
            cancellationToken));

    /// <summary>
    /// Replaces the complete managed-exception breakpoint policy.
    /// </summary>
    [McpServerTool(
        Name = "debug_exception_breakpoints_set",
        Title = "Set .NET exception breakpoints",
        Destructive = true,
        Idempotent = true,
        OpenWorld = true,
        ReadOnly = false,
        UseStructuredContent = true,
        OutputSchemaType = typeof(McpDebugExceptionBreakpointsResult))]
    [Description("Replace the managed-exception policy at an exact stopped generation. Requires an active debug_agent_control_set grant.")]
    public Task<ModelContextProtocol.Protocol.CallToolResult> SetExceptionBreakpointsAsync(
        [Description("Opaque identifier returned by a debugger lifecycle tool.")]
        string debugSession,
        [Description("Exact current positive stop generation.")]
        long stopGeneration,
        [Description("Complete replacement policy; an empty list disables exception stops.")]
        IReadOnlyList<McpDebugExceptionBreakpoint> breakpoints,
        CancellationToken cancellationToken) =>
        McpDebuggerToolResult.RunAsync(() => _broker.SetExceptionBreakpointsAsync(
            debugSession,
            stopGeneration,
            breakpoints,
            cancellationToken));
}
