using Csls.Control.Contracts;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using System.ComponentModel;
using System.Globalization;
using System.Text.Json;

namespace Csls.Mcp.Worker;

/// <summary>
/// Exposes workspace state and maintenance for explicitly selected csls sessions.
/// </summary>
[McpServerToolType]
internal sealed class CslsMcpWorkspaceTools
{
    private readonly McpSessionBroker _sessionBroker;

    /// <summary>
    /// Creates workspace tools backed by the shared MCP session broker.
    /// </summary>
    /// <param name="sessionBroker">The shared selector-aware session broker.</param>
    public CslsMcpWorkspaceTools(McpSessionBroker sessionBroker)
    {
        ArgumentNullException.ThrowIfNull(sessionBroker);
        _sessionBroker = sessionBroker;
    }

    /// <summary>
    /// Gets a compact selected-workspace overview with a resource link to complete details.
    /// </summary>
    /// <param name="cancellationToken">The MCP request cancellation token.</param>
    /// <param name="workspace">The optional workspace, project, or document path.</param>
    /// <param name="session">The optional language-server process identifier.</param>
    /// <param name="socket">The optional absolute control-socket path.</param>
    /// <returns>The compact workspace overview and detailed-resource link.</returns>
    [McpServerTool(
        Name = "get_workspace_state",
        Title = "Get csls workspace state",
        Destructive = false,
        Idempotent = true,
        OpenWorld = false,
        ReadOnly = true,
        UseStructuredContent = true,
        OutputSchemaType = typeof(McpWorkspaceSummary))]
    [Description("Get a compact workspace health overview. Follow the returned resource link only when complete project, document, request, cache, log, or diagnostic details are needed.")]
    public async Task<CallToolResult> GetWorkspaceStateAsync(
        CancellationToken cancellationToken,
        [Description("Workspace, project, or document path. Specify exactly one target selector.")]
        string? workspace = null,
        [Description("Language-server process identifier. Specify exactly one target selector.")]
        int? session = null,
        [Description("Absolute control-socket path. Specify exactly one target selector.")]
        string? socket = null)
    {
        ControlDashboardSnapshot snapshot = await _sessionBroker.InvokeAsync(
            workspace,
            session,
            socket,
            static (client, requestToken) => client.GetDashboardSnapshotAsync(
                new ControlDashboardRequest { IncludeDiagnostics = false },
                requestToken),
            cancellationToken).ConfigureAwait(false);
        string detailsUri = string.Create(
            CultureInfo.InvariantCulture,
            $"csls://workspace/?session={snapshot.Session.ProcessId}");
        var summary = new McpWorkspaceSummary
        {
            ProcessId = snapshot.Session.ProcessId,
            LifecycleState = snapshot.Session.LifecycleState,
            WorkspacePhase = snapshot.Session.WorkspacePhase,
            WorkspaceGeneration = snapshot.Session.WorkspaceGeneration,
            WorkspaceRootCount = snapshot.Session.WorkspaceRoots.Count,
            WorkspaceCount = snapshot.Workspaces.Count,
            ProjectCount = snapshot.Projects.Count,
            DocumentCount = snapshot.Documents.Count,
            AcceptedRequestCount = snapshot.Requests.AcceptedRequests,
            CompletedRequestCount = snapshot.Requests.CompletedRequests,
            ActiveRequestCount = snapshot.Requests.TotalActiveRequests,
            QueuedRequestCount = snapshot.Requests.QueuedRequests,
            IsMutationActive = snapshot.Requests.IsMutationActive,
            IsStopping = snapshot.Requests.IsStopping,
            BuildHostCount = snapshot.BuildHosts.Count,
            CacheCount = snapshot.Caches.Count,
            RetainedLogCount = snapshot.Logs.Count,
            DetailsUri = detailsUri
        };
        JsonElement structuredContent = JsonSerializer.SerializeToElement(
            summary,
            McpJsonSerializerContext.Default.McpWorkspaceSummary);
        return new CallToolResult
        {
            Content =
            [
                new TextContentBlock { Text = structuredContent.GetRawText() },
                new ResourceLinkBlock
                {
                    Uri = detailsUri,
                    Name = $"csls-workspace-{snapshot.Session.ProcessId}",
                    Title = "Complete csls workspace state",
                    Description =
                        "Complete project, document, request, build-host, cache, log, and diagnostic state for the selected session.",
                    MimeType = "application/json"
                }
            ],
            StructuredContent = structuredContent
        };
    }

    /// <summary>
    /// Runs dotnet restore for every workspace entry point and atomically reloads Roslyn state.
    /// </summary>
    /// <param name="cancellationToken">The MCP request cancellation token.</param>
    /// <param name="workspace">The optional workspace, project, or document path.</param>
    /// <param name="session">The optional language-server process identifier.</param>
    /// <param name="socket">The optional absolute control-socket path.</param>
    /// <returns>The completed restore result.</returns>
    [McpServerTool(
        Name = "restore_workspace",
        Title = "Restore csls workspace",
        Destructive = false,
        Idempotent = true,
        OpenWorld = false,
        ReadOnly = false,
        UseStructuredContent = true)]
    [Description("Run dotnet restore for every workspace entry point and atomically reload one selected Roslyn workspace.")]
    public Task<ControlWorkspaceOperationResult> RestoreWorkspaceAsync(
        CancellationToken cancellationToken,
        [Description("Workspace, project, or document path. Specify exactly one target selector.")]
        string? workspace = null,
        [Description("Language-server process identifier. Specify exactly one target selector.")]
        int? session = null,
        [Description("Absolute control-socket path. Specify exactly one target selector.")]
        string? socket = null) =>
        _sessionBroker.InvokeAsync(workspace, session, socket,
            static (client, requestToken) => client.RestoreWorkspaceAsync(requestToken),
            cancellationToken);

    /// <summary>
    /// Atomically reloads every workspace root while preserving open document overlays.
    /// </summary>
    /// <param name="cancellationToken">The MCP request cancellation token.</param>
    /// <param name="workspace">The optional workspace, project, or document path.</param>
    /// <param name="session">The optional language-server process identifier.</param>
    /// <param name="socket">The optional absolute control-socket path.</param>
    /// <returns>The completed reload result.</returns>
    [McpServerTool(
        Name = "reload_workspace",
        Title = "Reload csls workspace",
        Destructive = false,
        Idempotent = true,
        OpenWorld = false,
        ReadOnly = false,
        UseStructuredContent = true)]
    [Description("Atomically reload every root in one selected workspace while preserving unsaved overlays.")]
    public Task<ControlWorkspaceOperationResult> ReloadWorkspaceAsync(
        CancellationToken cancellationToken,
        [Description("Workspace, project, or document path. Specify exactly one target selector.")]
        string? workspace = null,
        [Description("Language-server process identifier. Specify exactly one target selector.")]
        int? session = null,
        [Description("Absolute control-socket path. Specify exactly one target selector.")]
        string? socket = null) =>
        _sessionBroker.InvokeAsync(workspace, session, socket,
            static (client, requestToken) => client.ReloadWorkspaceAsync(requestToken),
            cancellationToken);

    /// <summary>
    /// Recreates every Roslyn build host while preserving open document overlays.
    /// </summary>
    /// <param name="cancellationToken">The MCP request cancellation token.</param>
    /// <param name="workspace">The optional workspace, project, or document path.</param>
    /// <param name="session">The optional language-server process identifier.</param>
    /// <param name="socket">The optional absolute control-socket path.</param>
    /// <returns>The completed build-host restart result.</returns>
    [McpServerTool(
        Name = "restart_build_hosts",
        Title = "Restart csls build hosts",
        Destructive = false,
        Idempotent = true,
        OpenWorld = false,
        ReadOnly = false,
        UseStructuredContent = true)]
    [Description("Recreate every Roslyn host in one selected workspace while preserving unsaved overlays.")]
    public Task<ControlWorkspaceOperationResult> RestartBuildHostsAsync(
        CancellationToken cancellationToken,
        [Description("Workspace, project, or document path. Specify exactly one target selector.")]
        string? workspace = null,
        [Description("Language-server process identifier. Specify exactly one target selector.")]
        int? session = null,
        [Description("Absolute control-socket path. Specify exactly one target selector.")]
        string? socket = null) =>
        _sessionBroker.InvokeAsync(workspace, session, socket,
            static (client, requestToken) => client.RestartBuildHostsAsync(requestToken),
            cancellationToken);

    /// <summary>
    /// Clears retained diagnostic, semantic-token, and pending-edit cache entries.
    /// </summary>
    /// <param name="cancellationToken">The MCP request cancellation token.</param>
    /// <param name="workspace">The optional workspace, project, or document path.</param>
    /// <param name="session">The optional language-server process identifier.</param>
    /// <param name="socket">The optional absolute control-socket path.</param>
    /// <returns>The completed cache-clear result.</returns>
    [McpServerTool(
        Name = "clear_caches",
        Title = "Clear csls caches",
        Destructive = true,
        Idempotent = true,
        OpenWorld = false,
        ReadOnly = false,
        UseStructuredContent = true)]
    [Description("Clear retained result caches for one explicitly selected csls session.")]
    public Task<ControlWorkspaceOperationResult> ClearCachesAsync(
        CancellationToken cancellationToken,
        [Description("Workspace, project, or document path. Specify exactly one target selector.")]
        string? workspace = null,
        [Description("Language-server process identifier. Specify exactly one target selector.")]
        int? session = null,
        [Description("Absolute control-socket path. Specify exactly one target selector.")]
        string? socket = null) =>
        _sessionBroker.InvokeAsync(workspace, session, socket,
            static (client, requestToken) => client.ClearCachesAsync(requestToken),
            cancellationToken);
}
