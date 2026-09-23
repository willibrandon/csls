using ModelContextProtocol.Server;
using System.ComponentModel;

namespace Csls.Mcp.Worker;

/// <summary>
/// Exposes read-only authoritative debugger breakpoint inspection.
/// </summary>
internal sealed partial class CslsMcpDebuggerInspectionTools
{
    /// <summary>
    /// Gets every source, function, instruction, and exception breakpoint.
    /// </summary>
    [McpServerTool(
        Name = "debug_breakpoints_get",
        Title = "Get .NET debugger breakpoints",
        Destructive = false,
        Idempotent = true,
        OpenWorld = false,
        ReadOnly = true,
        UseStructuredContent = true,
        OutputSchemaType = typeof(McpDebugBreakpointsResult))]
    [Description("Get every authoritative breakpoint and exception policy for one explicit debugger session.")]
    public Task<ModelContextProtocol.Protocol.CallToolResult> GetBreakpointsAsync(
        [Description("Opaque identifier returned by a debugger lifecycle tool.")]
        string debugSession,
        CancellationToken cancellationToken) =>
        McpDebuggerToolResult.RunAsync(() =>
            _broker.GetBreakpointsAsync(debugSession, cancellationToken));
}
