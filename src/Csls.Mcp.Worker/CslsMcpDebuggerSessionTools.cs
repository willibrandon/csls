using ModelContextProtocol.Server;
using System.ComponentModel;

namespace Csls.Mcp.Worker;

/// <summary>
/// Exposes debugger session discovery and deterministic release.
/// </summary>
[McpServerToolType]
internal sealed class CslsMcpDebuggerSessionTools
{
    private readonly McpDebuggerSessionBroker _broker;

    /// <summary>
    /// Creates session tools backed by the connection-owned debugger broker.
    /// </summary>
    /// <param name="broker">The shared debugger-session broker.</param>
    public CslsMcpDebuggerSessionTools(McpDebuggerSessionBroker broker)
    {
        ArgumentNullException.ThrowIfNull(broker);
        _broker = broker;
    }

    /// <summary>
    /// Lists debugger sessions owned by this MCP connection.
    /// </summary>
    /// <param name="cancellationToken">The MCP request cancellation token.</param>
    /// <returns>The ordered current debugger-session list.</returns>
    [McpServerTool(
        Name = "debug_sessions_list",
        Title = "List .NET debugger sessions",
        Destructive = false,
        Idempotent = true,
        OpenWorld = false,
        ReadOnly = true,
        UseStructuredContent = true)]
    [Description("List only the explicit debugger sessions owned by this MCP connection.")]
    public Task<IReadOnlyList<McpDebugSessionInfo>> ListAsync(
        CancellationToken cancellationToken) =>
        _broker.ListAsync(cancellationToken);

    /// <summary>
    /// Gets current lifecycle state for one explicit debugger session.
    /// </summary>
    /// <param name="debugSession">The exact opaque debugger-session identifier.</param>
    /// <param name="cancellationToken">The MCP request cancellation token.</param>
    /// <returns>The current debugger-session state.</returns>
    [McpServerTool(
        Name = "debug_session_get",
        Title = "Get .NET debugger session",
        Destructive = false,
        Idempotent = true,
        OpenWorld = false,
        ReadOnly = true,
        UseStructuredContent = true,
        OutputSchemaType = typeof(McpDebugSessionInfo))]
    [Description("Get current state for exactly one debugSession; no active target is inferred.")]
    public Task<McpDebugSessionInfo> GetAsync(
        [Description("Opaque identifier returned by debug_session_start or debug_session_attach.")]
        string debugSession,
        CancellationToken cancellationToken) =>
        _broker.GetAsync(debugSession, cancellationToken);

    /// <summary>
    /// Ends and releases one explicit debugger session.
    /// </summary>
    /// <param name="debugSession">The exact opaque debugger-session identifier.</param>
    /// <param name="cancellationToken">The MCP request cancellation token.</param>
    /// <param name="terminateAttachedTarget">Whether an attached target is explicitly terminated.</param>
    /// <returns>The terminal debugger-session state.</returns>
    [McpServerTool(
        Name = "debug_session_end",
        Title = "End .NET debugger session",
        Destructive = true,
        Idempotent = false,
        OpenWorld = true,
        ReadOnly = false,
        UseStructuredContent = true,
        OutputSchemaType = typeof(McpDebugSessionInfo))]
    [Description("End one explicit debugger session. Launched targets terminate; attached targets detach unless terminateAttachedTarget is explicitly requested.")]
    public Task<McpDebugSessionInfo> EndAsync(
        [Description("Opaque identifier returned by debug_session_start or debug_session_attach.")]
        string debugSession,
        CancellationToken cancellationToken,
        [Description("Explicitly terminate an attached target instead of safely detaching it.")]
        bool terminateAttachedTarget = false) =>
        _broker.EndAsync(debugSession, terminateAttachedTarget, cancellationToken);
}
