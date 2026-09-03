using ModelContextProtocol.Server;
using System.ComponentModel;

namespace Csls.Mcp.Worker;

/// <summary>
/// Exposes explicitly authorized debugger restart control.
/// </summary>
internal sealed partial class CslsMcpDebuggerExecutionTools
{
    /// <summary>
    /// Restarts a stopped debugger target with its original activation request.
    /// </summary>
    /// <param name="debugSession">The exact opaque debugger-session identifier.</param>
    /// <param name="stopGeneration">The exact current stopped generation.</param>
    /// <param name="cancellationToken">The MCP request cancellation token.</param>
    /// <returns>The replacement debugger-target state.</returns>
    [McpServerTool(
        Name = "debug_session_restart",
        Title = "Restart .NET debugger target",
        Destructive = true,
        Idempotent = false,
        OpenWorld = true,
        ReadOnly = false,
        UseStructuredContent = true,
        OutputSchemaType = typeof(McpDebugSessionInfo))]
    [Description("Restart one stopped debugger target with its original launch or attach request. Requires an active debug_agent_control_set grant and the exact stopGeneration.")]
    public Task<ModelContextProtocol.Protocol.CallToolResult> RestartAsync(
        [Description("Opaque identifier returned by a debugger lifecycle tool.")]
        string debugSession,
        [Description("Exact positive stop generation returned by debug_session_get.")]
        long stopGeneration,
        CancellationToken cancellationToken) =>
        McpDebuggerToolResult.RunAsync(() => _broker.RestartAsync(
            debugSession,
            stopGeneration,
            cancellationToken));
}
