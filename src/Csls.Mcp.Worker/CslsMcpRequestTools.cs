using Csls.Control.Contracts;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using System.ComponentModel;

namespace Csls.Mcp.Worker;

/// <summary>
/// Exposes live request cancellation and bounded tracing for selected csls sessions.
/// </summary>
[McpServerToolType]
internal sealed class CslsMcpRequestTools
{
    private readonly McpSessionBroker _sessionBroker;

    /// <summary>
    /// Creates request tools backed by the shared MCP session broker.
    /// </summary>
    /// <param name="sessionBroker">The shared selector-aware session broker.</param>
    public CslsMcpRequestTools(McpSessionBroker sessionBroker)
    {
        ArgumentNullException.ThrowIfNull(sessionBroker);
        _sessionBroker = sessionBroker;
    }

    /// <summary>
    /// Gets bounded queued and running request state with current trace information.
    /// </summary>
    /// <param name="cancellationToken">The MCP request cancellation token.</param>
    /// <param name="workspace">The optional workspace, project, or document path.</param>
    /// <param name="session">The optional language-server process identifier.</param>
    /// <param name="socket">The optional absolute control-socket path.</param>
    /// <returns>The live bounded scheduler observation.</returns>
    [McpServerTool(Name = "list_requests", Title = "List csls requests", Destructive = false, Idempotent = true, OpenWorld = false, ReadOnly = true, UseStructuredContent = true)]
    [Description("List bounded queued and running requests for one selected csls session.")]
    public Task<ControlRequestSchedulerInfo> ListRequestsAsync(
        CancellationToken cancellationToken,
        [Description("Workspace, project, or document path. Specify exactly one target selector.")]
        string? workspace = null,
        [Description("Language-server process identifier. Specify exactly one target selector.")]
        int? session = null,
        [Description("Absolute control-socket path. Specify exactly one target selector.")]
        string? socket = null) =>
        _sessionBroker.InvokeAsync(
            workspace,
            session,
            socket,
            static async (client, requestToken) =>
            {
                ControlDashboardSnapshot dashboard = await client.GetDashboardSnapshotAsync(
                    new ControlDashboardRequest { IncludeDiagnostics = false },
                    requestToken).ConfigureAwait(false);
                return dashboard.Requests;
            },
            cancellationToken);

    /// <summary>
    /// Attempts to cancel one live request by its stable correlation identifier.
    /// </summary>
    /// <param name="correlationId">The stable request correlation identifier.</param>
    /// <param name="cancellationToken">The MCP request cancellation token.</param>
    /// <param name="workspace">The optional workspace, project, or document path.</param>
    /// <param name="session">The optional language-server process identifier.</param>
    /// <param name="socket">The optional absolute control-socket path.</param>
    /// <returns>The deterministic request cancellation result.</returns>
    [McpServerTool(Name = "cancel_request", Title = "Cancel csls request", Destructive = true, Idempotent = true, OpenWorld = false, ReadOnly = false, UseStructuredContent = true)]
    [Description("Cancel one queued or running request in one selected csls session.")]
    public Task<ControlCancelRequestResult> CancelRequestAsync(
        [Description("Correlation identifier returned by list_requests.")]
        string correlationId,
        CancellationToken cancellationToken,
        [Description("Workspace, project, or document path. Specify exactly one target selector.")]
        string? workspace = null,
        [Description("Language-server process identifier. Specify exactly one target selector.")]
        int? session = null,
        [Description("Absolute control-socket path. Specify exactly one target selector.")]
        string? socket = null)
    {
        if (!Guid.TryParseExact(correlationId, "D", out Guid parsedCorrelationId))
        {
            throw new McpException("correlationId must be a GUID in D format.");
        }

        return _sessionBroker.InvokeAsync(
            workspace,
            session,
            socket,
            (client, requestToken) => client.CancelRequestAsync(
                new ControlCancelRequest { CorrelationId = parsedCorrelationId },
                requestToken),
            cancellationToken);
    }

    /// <summary>
    /// Starts one bounded request lifecycle trace for a selected session.
    /// </summary>
    /// <param name="cancellationToken">The MCP request cancellation token.</param>
    /// <param name="workspace">The optional workspace, project, or document path.</param>
    /// <param name="session">The optional language-server process identifier.</param>
    /// <param name="socket">The optional absolute control-socket path.</param>
    /// <returns>The newly active trace observation.</returns>
    [McpServerTool(Name = "start_trace", Title = "Start csls trace", Destructive = false, Idempotent = false, OpenWorld = false, ReadOnly = false, UseStructuredContent = true)]
    [Description("Start one bounded request lifecycle trace for one selected csls session.")]
    public Task<ControlTraceInfo> StartTraceAsync(
        CancellationToken cancellationToken,
        [Description("Workspace, project, or document path. Specify exactly one target selector.")]
        string? workspace = null,
        [Description("Language-server process identifier. Specify exactly one target selector.")]
        int? session = null,
        [Description("Absolute control-socket path. Specify exactly one target selector.")]
        string? socket = null) =>
        _sessionBroker.InvokeAsync(workspace, session, socket,
            static (client, requestToken) => client.StartTraceAsync(requestToken),
            cancellationToken);

    /// <summary>
    /// Stops the active bounded request lifecycle trace for a selected session.
    /// </summary>
    /// <param name="cancellationToken">The MCP request cancellation token.</param>
    /// <param name="workspace">The optional workspace, project, or document path.</param>
    /// <param name="session">The optional language-server process identifier.</param>
    /// <param name="socket">The optional absolute control-socket path.</param>
    /// <returns>The stopped trace observation and retained entries.</returns>
    [McpServerTool(Name = "stop_trace", Title = "Stop csls trace", Destructive = false, Idempotent = false, OpenWorld = false, ReadOnly = false, UseStructuredContent = true)]
    [Description("Stop the active request lifecycle trace for one selected csls session.")]
    public Task<ControlTraceInfo> StopTraceAsync(
        CancellationToken cancellationToken,
        [Description("Workspace, project, or document path. Specify exactly one target selector.")]
        string? workspace = null,
        [Description("Language-server process identifier. Specify exactly one target selector.")]
        int? session = null,
        [Description("Absolute control-socket path. Specify exactly one target selector.")]
        string? socket = null) =>
        _sessionBroker.InvokeAsync(workspace, session, socket,
            static (client, requestToken) => client.StopTraceAsync(requestToken),
            cancellationToken);
}
