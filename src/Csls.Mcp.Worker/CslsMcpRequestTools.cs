using Csls.Control;
using Csls.Control.Contracts;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using System.ComponentModel;

namespace Csls.Mcp.Worker;

/// <summary>
/// Exposes live request cancellation and bounded tracing through shared control services.
/// </summary>
[McpServerToolType]
internal sealed class CslsMcpRequestTools
{
    private readonly ControlRpcClient _controlClient;

    /// <summary>
    /// Creates request tools backed by the attached versioned control client.
    /// </summary>
    /// <param name="controlClient">The attached session control client.</param>
    public CslsMcpRequestTools(ControlRpcClient controlClient)
    {
        ArgumentNullException.ThrowIfNull(controlClient);
        _controlClient = controlClient;
    }

    /// <summary>
    /// Gets bounded queued and running request state with current trace information.
    /// </summary>
    /// <param name="cancellationToken">The MCP request cancellation token.</param>
    /// <returns>The live bounded scheduler observation.</returns>
    [McpServerTool(
        Name = "list_requests",
        Title = "List csls requests",
        Destructive = false,
        Idempotent = true,
        OpenWorld = false,
        ReadOnly = true,
        UseStructuredContent = true)]
    [Description("List bounded queued and running requests with correlation identifiers and current trace state.")]
    public async Task<ControlRequestSchedulerInfo> ListRequestsAsync(
        CancellationToken cancellationToken)
    {
        ControlDashboardSnapshot dashboard = await _controlClient.GetDashboardSnapshotAsync(
            new ControlDashboardRequest { IncludeDiagnostics = false },
            cancellationToken).ConfigureAwait(false);
        return dashboard.Requests;
    }

    /// <summary>
    /// Attempts to cancel one live request by its stable correlation identifier.
    /// </summary>
    /// <param name="correlationId">The stable request correlation identifier.</param>
    /// <param name="cancellationToken">The MCP request cancellation token.</param>
    /// <returns>The deterministic request cancellation result.</returns>
    [McpServerTool(
        Name = "cancel_request",
        Title = "Cancel csls request",
        Destructive = true,
        Idempotent = true,
        OpenWorld = false,
        ReadOnly = false,
        UseStructuredContent = true)]
    [Description("Cancel one queued or running csls request by its stable correlation identifier.")]
    public Task<ControlCancelRequestResult> CancelRequestAsync(
        [Description("Correlation identifier returned by list_requests.")]
        string correlationId,
        CancellationToken cancellationToken)
    {
        if (!Guid.TryParseExact(correlationId, "D", out Guid parsedCorrelationId))
        {
            throw new McpException("correlationId must be a GUID in D format.");
        }

        return _controlClient.CancelRequestAsync(
            new ControlCancelRequest { CorrelationId = parsedCorrelationId },
            cancellationToken);
    }

    /// <summary>
    /// Starts one bounded request lifecycle trace for the attached session.
    /// </summary>
    /// <param name="cancellationToken">The MCP request cancellation token.</param>
    /// <returns>The newly active trace observation.</returns>
    [McpServerTool(
        Name = "start_trace",
        Title = "Start csls trace",
        Destructive = false,
        Idempotent = false,
        OpenWorld = false,
        ReadOnly = false,
        UseStructuredContent = true)]
    [Description("Start one bounded request lifecycle trace for the attached csls session.")]
    public Task<ControlTraceInfo> StartTraceAsync(CancellationToken cancellationToken) =>
        _controlClient.StartTraceAsync(cancellationToken);

    /// <summary>
    /// Stops the active bounded request lifecycle trace for the attached session.
    /// </summary>
    /// <param name="cancellationToken">The MCP request cancellation token.</param>
    /// <returns>The stopped trace observation and retained entries.</returns>
    [McpServerTool(
        Name = "stop_trace",
        Title = "Stop csls trace",
        Destructive = false,
        Idempotent = false,
        OpenWorld = false,
        ReadOnly = false,
        UseStructuredContent = true)]
    [Description("Stop the active request lifecycle trace and return its bounded retained entries.")]
    public Task<ControlTraceInfo> StopTraceAsync(CancellationToken cancellationToken) =>
        _controlClient.StopTraceAsync(cancellationToken);
}
