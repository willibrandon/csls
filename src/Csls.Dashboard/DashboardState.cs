using Csls.Control;
using Csls.Control.Contracts;
using StreamJsonRpc;
using System.Net.Sockets;
using System.Runtime.CompilerServices;

namespace Csls.Dashboard;

/// <summary>
/// Owns the selected live session and immutable control snapshots rendered by Hex1b.
/// </summary>
internal sealed class DashboardState
{
    private readonly CancellationToken _cancellationToken;

    private DashboardState(CancellationToken cancellationToken)
    {
        _cancellationToken = cancellationToken;
    }

    /// <summary>
    /// Gets the currently selected dashboard section.
    /// </summary>
    internal DashboardSection Section { get; private set; }

    /// <summary>
    /// Gets the responsive live sessions in process-identifier order.
    /// </summary>
    internal IReadOnlyList<ControlSessionInfo> Sessions { get; private set; } = [];

    /// <summary>
    /// Gets the current selected-session control snapshot.
    /// </summary>
    internal ControlDashboardSnapshot Snapshot { get; private set; } = null!;

    /// <summary>
    /// Gets the UTC time at which the selected-session snapshot completed.
    /// </summary>
    internal DateTimeOffset RefreshedAt { get; private set; }

    /// <summary>
    /// Gets the most recent workspace operation status shown to the user.
    /// </summary>
    internal string OperationStatus { get; private set; } = "No workspace operation has run.";

    /// <summary>
    /// Creates state by discovering sessions and loading the requested live process.
    /// </summary>
    /// <param name="processId">The requested worker process, or zero to infer one.</param>
    /// <param name="workspacePath">The optional workspace path used to select or validate a session.</param>
    /// <param name="cancellationToken">The dashboard cancellation token.</param>
    /// <returns>The initialized dashboard state.</returns>
    internal static async Task<DashboardState> CreateAsync(
        int processId,
        string? workspacePath,
        CancellationToken cancellationToken)
    {
        var state = new DashboardState(cancellationToken);
        await state.RefreshSessionsAsync(processId, workspacePath).ConfigureAwait(false);
        return state;
    }

    /// <summary>
    /// Refreshes discovery and the currently selected live session.
    /// </summary>
    /// <returns>A task that completes after real control RPC calls finish.</returns>
    internal async Task RefreshAsync()
    {
        OperationStatus = "Refreshing live session state...";
        await RefreshSessionsAsync(
            Snapshot.Session.ProcessId,
            workspacePath: null).ConfigureAwait(false);
        OperationStatus = "Refreshed live session state.";
    }

    /// <summary>
    /// Selects one dashboard section and evaluates diagnostics only when requested.
    /// </summary>
    /// <param name="section">The selected dashboard section.</param>
    /// <returns>A task that completes after any required real control request.</returns>
    internal async Task SelectSectionAsync(DashboardSection section)
    {
        Section = section;
        if (section == DashboardSection.Diagnostics && !Snapshot.DiagnosticsLoaded)
        {
            await LoadSnapshotAsync(
                Snapshot.Session.ProcessId,
                includeDiagnostics: true).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Selects and loads one discovered live language-server session.
    /// </summary>
    /// <param name="processId">The selected worker process identifier.</param>
    /// <returns>A task that completes after the real control snapshot arrives.</returns>
    internal Task SelectSessionAsync(int processId) => LoadSnapshotAsync(
        processId,
        includeDiagnostics: Section == DashboardSection.Diagnostics);

    /// <summary>
    /// Executes one user-confirmed workspace mutation through the shared control service.
    /// </summary>
    /// <param name="operation">The confirmed operation to execute.</param>
    /// <returns>A task that completes after the operation and dashboard refresh finish.</returns>
    internal async Task ExecuteOperationAsync(DashboardOperation operation)
    {
        int processId = Snapshot.Session.ProcessId;
        OperationStatus = $"Running {GetOperationName(operation)}...";
        try
        {
            var client = new ControlRpcClient(Snapshot.Session.SocketPath);
            await using ConfiguredAsyncDisposable cleanup = client.ConfigureAwait(false);
            ControlWorkspaceOperationResult result = operation switch
            {
                DashboardOperation.Restore => await client
                    .RestoreWorkspaceAsync(_cancellationToken)
                    .ConfigureAwait(false),
                DashboardOperation.Reload => await client
                    .ReloadWorkspaceAsync(_cancellationToken)
                    .ConfigureAwait(false),
                DashboardOperation.RestartBuildHosts => await client
                    .RestartBuildHostsAsync(_cancellationToken)
                    .ConfigureAwait(false),
                DashboardOperation.ClearCaches => await client
                    .ClearCachesAsync(_cancellationToken)
                    .ConfigureAwait(false),
                _ => throw new InvalidOperationException(
                    $"Unknown dashboard operation: {operation}.")
            };
            OperationStatus = $"Completed {result.Operation}; generation " +
                $"{result.PreviousGeneration} -> {result.CurrentGeneration}; " +
                $"cleared {result.ClearedCacheEntryCount} cache entries.";
            await LoadSnapshotAsync(
                processId,
                includeDiagnostics: Section == DashboardSection.Diagnostics).ConfigureAwait(false);
        }
        catch (Exception exception) when (
            exception is IOException or
                InvalidDataException or
                UnauthorizedAccessException or
                InvalidOperationException or
                SocketException or
                RemoteInvocationException)
        {
            OperationStatus = $"Operation failed: {exception.Message}";
        }
    }

    /// <summary>
    /// Cancels one user-confirmed live request through the shared control service.
    /// </summary>
    /// <param name="correlationId">The live request correlation identifier.</param>
    /// <returns>A task that completes after cancellation and dashboard refresh finish.</returns>
    internal async Task CancelRequestAsync(Guid correlationId)
    {
        int processId = Snapshot.Session.ProcessId;
        OperationStatus = $"Canceling request {correlationId:D}...";
        try
        {
            var client = new ControlRpcClient(Snapshot.Session.SocketPath);
            await using ConfiguredAsyncDisposable cleanup = client.ConfigureAwait(false);
            ControlCancelRequestResult result = await client.CancelRequestAsync(
                new ControlCancelRequest { CorrelationId = correlationId },
                _cancellationToken).ConfigureAwait(false);
            OperationStatus = result.CancellationRequested
                ? $"Cancellation requested for {correlationId:D}."
                : $"Request {correlationId:D} is no longer active.";
            await LoadSnapshotAsync(processId, includeDiagnostics: false).ConfigureAwait(false);
        }
        catch (Exception exception) when (IsControlException(exception))
        {
            OperationStatus = $"Cancellation failed: {exception.Message}";
        }
    }

    /// <summary>
    /// Starts or stops user-confirmed request tracing through the shared control service.
    /// </summary>
    /// <param name="start">Whether to start rather than stop tracing.</param>
    /// <returns>A task that completes after trace mutation and dashboard refresh finish.</returns>
    internal async Task SetTraceAsync(bool start)
    {
        int processId = Snapshot.Session.ProcessId;
        OperationStatus = start ? "Starting request trace..." : "Stopping request trace...";
        try
        {
            var client = new ControlRpcClient(Snapshot.Session.SocketPath);
            await using ConfiguredAsyncDisposable cleanup = client.ConfigureAwait(false);
            ControlTraceInfo trace = start
                ? await client.StartTraceAsync(_cancellationToken).ConfigureAwait(false)
                : await client.StopTraceAsync(_cancellationToken).ConfigureAwait(false);
            OperationStatus = trace.TraceId is Guid traceId
                ? $"Request trace {traceId:D} is {(trace.IsActive ? "active" : "stopped")}."
                : "No request trace is available.";
            await LoadSnapshotAsync(processId, includeDiagnostics: false).ConfigureAwait(false);
        }
        catch (Exception exception) when (IsControlException(exception))
        {
            OperationStatus = $"Trace operation failed: {exception.Message}";
        }
    }

    private async Task RefreshSessionsAsync(int processId, string? workspacePath)
    {
        Sessions = await ControlSessionDiscovery
            .DiscoverAsync(_cancellationToken)
            .ConfigureAwait(false);
        ControlSessionInfo selected = await ControlSessionDiscovery.ResolveAsync(
            processId,
            workspacePath,
            _cancellationToken).ConfigureAwait(false);
        if (!Sessions.Any(session => session.ProcessId == selected.ProcessId))
        {
            Sessions =
            [
                .. Sessions
                    .Append(selected)
                    .OrderBy(static session => session.ProcessId)
            ];
        }

        await SelectSessionAsync(selected.ProcessId).ConfigureAwait(false);
    }

    private async Task LoadSnapshotAsync(int processId, bool includeDiagnostics)
    {
        ControlSessionInfo selected = Sessions.FirstOrDefault(
            session => session.ProcessId == processId)
            ?? throw new InvalidOperationException($"Session {processId} is no longer live.");
        var client = new ControlRpcClient(selected.SocketPath);
        await using ConfiguredAsyncDisposable cleanup = client.ConfigureAwait(false);
        Snapshot = await client
            .GetDashboardSnapshotAsync(
                new ControlDashboardRequest { IncludeDiagnostics = includeDiagnostics },
                _cancellationToken)
            .ConfigureAwait(false);
        RefreshedAt = DateTimeOffset.UtcNow;
    }

    private static string GetOperationName(DashboardOperation operation) => operation switch
    {
        DashboardOperation.Restore => "restore",
        DashboardOperation.Reload => "reload",
        DashboardOperation.RestartBuildHosts => "restart build hosts",
        DashboardOperation.ClearCaches => "clear caches",
        _ => throw new InvalidOperationException($"Unknown dashboard operation: {operation}.")
    };

    private static bool IsControlException(Exception exception) =>
        exception is IOException or
            InvalidDataException or
            UnauthorizedAccessException or
            InvalidOperationException or
            SocketException or
            RemoteInvocationException;
}
