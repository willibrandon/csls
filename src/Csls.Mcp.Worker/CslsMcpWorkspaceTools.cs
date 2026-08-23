using Csls.Control;
using Csls.Control.Contracts;
using ModelContextProtocol.Server;
using System.ComponentModel;

namespace Csls.Mcp.Worker;

/// <summary>
/// Exposes workspace state and maintenance through the shared csls control service.
/// </summary>
[McpServerToolType]
internal sealed class CslsMcpWorkspaceTools
{
    private readonly ControlRpcClient _controlClient;

    /// <summary>
    /// Creates workspace tools backed by the attached versioned control client.
    /// </summary>
    /// <param name="controlClient">The attached session control client.</param>
    public CslsMcpWorkspaceTools(ControlRpcClient controlClient)
    {
        ArgumentNullException.ThrowIfNull(controlClient);
        _controlClient = controlClient;
    }

    /// <summary>
    /// Gets bounded workspace, project, document, request, host, cache, log, and diagnostic state.
    /// </summary>
    /// <param name="includeDiagnostics">Whether current compiler and analyzer diagnostics are evaluated.</param>
    /// <param name="cancellationToken">The MCP request cancellation token.</param>
    /// <returns>The current bounded workspace snapshot.</returns>
    [McpServerTool(
        Name = "get_workspace_state",
        Title = "Get csls workspace state",
        Destructive = false,
        Idempotent = true,
        OpenWorld = false,
        ReadOnly = true,
        UseStructuredContent = true)]
    [Description("Get bounded workspace, project, document, request, build-host, cache, log, and optional diagnostic state.")]
    public Task<ControlDashboardSnapshot> GetWorkspaceStateAsync(
        [Description("Evaluate current compiler and analyzer diagnostics for the snapshot.")]
        bool includeDiagnostics,
        CancellationToken cancellationToken) =>
        _controlClient.GetDashboardSnapshotAsync(
            new ControlDashboardRequest { IncludeDiagnostics = includeDiagnostics },
            cancellationToken);

    /// <summary>
    /// Runs dotnet restore for every current workspace entry point and atomically reloads Roslyn state.
    /// </summary>
    /// <param name="cancellationToken">The MCP request cancellation token.</param>
    /// <returns>The completed restore result.</returns>
    [McpServerTool(
        Name = "restore_workspace",
        Title = "Restore csls workspace",
        Destructive = false,
        Idempotent = true,
        OpenWorld = false,
        ReadOnly = false,
        UseStructuredContent = true)]
    [Description("Run dotnet restore for every workspace entry point and atomically reload Roslyn state while preserving open overlays.")]
    public Task<ControlWorkspaceOperationResult> RestoreWorkspaceAsync(
        CancellationToken cancellationToken) =>
        _controlClient.RestoreWorkspaceAsync(cancellationToken);

    /// <summary>
    /// Atomically reloads every workspace root while preserving open document overlays.
    /// </summary>
    /// <param name="cancellationToken">The MCP request cancellation token.</param>
    /// <returns>The completed reload result.</returns>
    [McpServerTool(
        Name = "reload_workspace",
        Title = "Reload csls workspace",
        Destructive = false,
        Idempotent = true,
        OpenWorld = false,
        ReadOnly = false,
        UseStructuredContent = true)]
    [Description("Atomically reload every workspace root while preserving unsaved open document overlays.")]
    public Task<ControlWorkspaceOperationResult> ReloadWorkspaceAsync(
        CancellationToken cancellationToken) =>
        _controlClient.ReloadWorkspaceAsync(cancellationToken);

    /// <summary>
    /// Recreates every Roslyn build host while preserving open document overlays.
    /// </summary>
    /// <param name="cancellationToken">The MCP request cancellation token.</param>
    /// <returns>The completed build-host restart result.</returns>
    [McpServerTool(
        Name = "restart_build_hosts",
        Title = "Restart csls build hosts",
        Destructive = false,
        Idempotent = true,
        OpenWorld = false,
        ReadOnly = false,
        UseStructuredContent = true)]
    [Description("Recreate every Roslyn workspace host while preserving unsaved open document overlays.")]
    public Task<ControlWorkspaceOperationResult> RestartBuildHostsAsync(
        CancellationToken cancellationToken) =>
        _controlClient.RestartBuildHostsAsync(cancellationToken);

    /// <summary>
    /// Clears retained diagnostic, semantic-token, and pending-edit cache entries.
    /// </summary>
    /// <param name="cancellationToken">The MCP request cancellation token.</param>
    /// <returns>The completed cache-clear result.</returns>
    [McpServerTool(
        Name = "clear_caches",
        Title = "Clear csls caches",
        Destructive = true,
        Idempotent = true,
        OpenWorld = false,
        ReadOnly = false,
        UseStructuredContent = true)]
    [Description("Clear retained diagnostic, semantic-token, and pending-edit cache entries for the attached session.")]
    public Task<ControlWorkspaceOperationResult> ClearCachesAsync(
        CancellationToken cancellationToken) =>
        _controlClient.ClearCachesAsync(cancellationToken);
}
