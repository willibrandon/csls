using ModelContextProtocol.Server;
using System.ComponentModel;

namespace Csls.Mcp.Worker;

/// <summary>
/// Exposes explicit revocable debugger authorization through MCP.
/// </summary>
[McpServerToolType]
internal sealed class CslsMcpDebuggerAuthorizationTools
{
    private readonly McpDebuggerSessionBroker _broker;

    /// <summary>
    /// Creates authorization tools backed by the connection-owned debugger broker.
    /// </summary>
    /// <param name="broker">The shared debugger-session broker.</param>
    public CslsMcpDebuggerAuthorizationTools(McpDebuggerSessionBroker broker)
    {
        ArgumentNullException.ThrowIfNull(broker);
        _broker = broker;
    }

    /// <summary>
    /// Grants or revokes time-bounded target-changing authority for one session.
    /// </summary>
    /// <param name="debugSession">The exact opaque debugger-session identifier.</param>
    /// <param name="enabled">Whether target-changing authority should be active.</param>
    /// <param name="cancellationToken">The MCP request cancellation token.</param>
    /// <param name="durationSeconds">The required positive grant duration when enabling control.</param>
    /// <returns>The selected session and resulting authorization state.</returns>
    [McpServerTool(
        Name = "debug_agent_control_set",
        Title = "Set .NET debugger agent control",
        Destructive = false,
        Idempotent = false,
        OpenWorld = false,
        ReadOnly = false,
        UseStructuredContent = true,
        OutputSchemaType = typeof(McpDebugSessionInfo))]
    [Description("Grant or revoke time-bounded target-changing authority for one explicit debugSession. Grants are connection-local and never inherited.")]
    public Task<ModelContextProtocol.Protocol.CallToolResult> SetAsync(
        [Description("Opaque identifier returned by debug_session_start or debug_session_attach.")]
        string debugSession,
        [Description("True to grant target-changing authority; false to revoke it immediately.")]
        bool enabled,
        CancellationToken cancellationToken,
        [Description("Required grant duration from 1 through 3600 seconds when enabled; omit when revoking.")]
        int? durationSeconds = null) =>
        McpDebuggerToolResult.RunAsync(() =>
            _broker.SetAgentControlAsync(
                debugSession,
                enabled,
                durationSeconds,
                cancellationToken));
}
