using Csls.Control;
using Csls.Control.Contracts;
using Hex1b;
using StreamJsonRpc;
using System.Net.Sockets;
using System.Runtime.CompilerServices;

namespace Csls.Dashboard;

/// <summary>
/// Owns the selected live session and immutable control snapshots rendered by Hex1b.
/// </summary>
internal sealed class DashboardState : IAsyncDisposable
{
    private readonly CancellationToken _cancellationToken;
    private readonly CancellationTokenSource _lifetimeCancellation;
    private readonly Dictionary<(DashboardSection Section, int Column), int> _columnWidths = [];
    private readonly Dictionary<DashboardSection, object> _focusedRows = [];
    private Task _diagnosticsLoadTask = Task.CompletedTask;
    private Hex1bApp? _app;
    private bool _diagnosticsLoading;
    private DashboardSection? _yankFlashSection;
    private object? _yankFlashKey;
    private long _yankFlashGeneration;
    private string? _focusedDiagnosticKey;
    private string? _diagnosticsLoadError;

    private DashboardState(CancellationToken cancellationToken)
    {
        _lifetimeCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken);
        _cancellationToken = _lifetimeCancellation.Token;
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
    /// Gets whether the first diagnostics snapshot is loading in the background.
    /// </summary>
    internal bool DiagnosticsLoading => _diagnosticsLoading;

    /// <summary>
    /// Gets the most recent background diagnostics loading error.
    /// </summary>
    internal string? DiagnosticsLoadError => _diagnosticsLoadError;

    /// <summary>
    /// Gets the stable key for the focused diagnostic row.
    /// </summary>
    internal string? FocusedDiagnosticKey => _focusedDiagnosticKey;

    /// <summary>
    /// Gets the focused diagnostic or the first available diagnostic.
    /// </summary>
    internal ControlDiagnosticInfo? FocusedDiagnostic => Snapshot.Diagnostics.FirstOrDefault(
        diagnostic => GetDiagnosticKey(diagnostic) == _focusedDiagnosticKey)
        ?? (Snapshot.Diagnostics.Count == 0 ? null : Snapshot.Diagnostics[0]);

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
    /// Attaches the running Hex1b application used to invalidate external state changes.
    /// </summary>
    /// <param name="app">The running dashboard application.</param>
    internal void AttachApp(Hex1bApp app)
    {
        ArgumentNullException.ThrowIfNull(app);
        if (_app is not null && !ReferenceEquals(_app, app))
        {
            throw new InvalidOperationException("The dashboard application is already attached.");
        }

        _app = app;
    }

    /// <summary>
    /// Gets the current width of one resizable table column.
    /// </summary>
    /// <param name="section">The table section.</param>
    /// <param name="column">The zero-based column index.</param>
    /// <param name="defaultWidth">The initial column width.</param>
    /// <returns>The current column width.</returns>
    internal int GetColumnWidth(
        DashboardSection section,
        int column,
        int defaultWidth) => _columnWidths.TryGetValue((section, column), out int width)
            ? width
            : defaultWidth;

    /// <summary>
    /// Resizes one table column from its drag-start width and redraws the dashboard.
    /// </summary>
    /// <param name="section">The table section.</param>
    /// <param name="column">The zero-based column index.</param>
    /// <param name="startWidth">The width at the start of the drag.</param>
    /// <param name="delta">The horizontal drag delta.</param>
    internal void ResizeColumn(
        DashboardSection section,
        int column,
        int startWidth,
        int delta)
    {
        _columnWidths[(section, column)] = Math.Clamp(startWidth + delta, 4, 120);
        _app?.Invalidate();
    }

    /// <summary>
    /// Reports whether the identified focused row is showing the transient yank flash.
    /// </summary>
    /// <param name="section">The table section.</param>
    /// <param name="key">The stable row key.</param>
    /// <returns><see langword="true"/> when the row should use yank colors.</returns>
    internal bool IsYankFlashing(DashboardSection section, object key) =>
        _yankFlashSection == section && Equals(_yankFlashKey, key);

    /// <summary>
    /// Flashes one yanked table row using the attached application renderer.
    /// </summary>
    /// <param name="section">The table section.</param>
    /// <param name="key">The stable row key.</param>
    internal void FlashYankedRow(DashboardSection section, object key)
    {
        long generation = ++_yankFlashGeneration;
        _yankFlashSection = section;
        _yankFlashKey = key;
        _app?.Invalidate();
        _ = Task.Delay(TimeSpan.FromMilliseconds(150), _cancellationToken).ContinueWith(
            _ =>
            {
                if (_yankFlashGeneration == generation)
                {
                    _yankFlashSection = null;
                    _yankFlashKey = null;
                    _app?.Invalidate();
                }
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    /// <summary>
    /// Gets the stable focused row key for one table section.
    /// </summary>
    /// <param name="section">The table section.</param>
    /// <param name="fallback">The first available row key.</param>
    /// <returns>The focused row key, or the supplied fallback.</returns>
    internal object? GetFocusedRow(DashboardSection section, object? fallback) =>
        _focusedRows.TryGetValue(section, out object? key) ? key : fallback;

    /// <summary>
    /// Records the stable focused row key for one table section.
    /// </summary>
    /// <param name="section">The table section.</param>
    /// <param name="key">The focused row key, or <see langword="null"/>.</param>
    internal void SetFocusedRow(DashboardSection section, object? key)
    {
        if (key is null)
        {
            _focusedRows.Remove(section);
            return;
        }

        _focusedRows[section] = key;
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
    internal Task SelectSectionAsync(DashboardSection section)
    {
        Section = section;
        if (section == DashboardSection.Diagnostics &&
            !Snapshot.DiagnosticsLoaded &&
            !_diagnosticsLoading)
        {
            _diagnosticsLoading = true;
            _diagnosticsLoadError = null;
            _diagnosticsLoadTask = Task.Run(
                () => LoadDiagnosticsAsync(Snapshot.Session.ProcessId),
                CancellationToken.None);
        }

        _app?.Invalidate();
        return Task.CompletedTask;
    }

    /// <summary>
    /// Cancels and observes pending dashboard work before the application exits.
    /// </summary>
    /// <returns>A task that completes after pending work observes cancellation.</returns>
    public async ValueTask DisposeAsync()
    {
        await _lifetimeCancellation.CancelAsync().ConfigureAwait(false);
        Task diagnosticsCompletion = _diagnosticsLoadTask.ContinueWith(
            static task => task.GetAwaiter().GetResult(),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
        try
        {
            await diagnosticsCompletion.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_cancellationToken.IsCancellationRequested)
        {
            _diagnosticsLoadError = null;
        }

        _lifetimeCancellation.Dispose();
        GC.SuppressFinalize(this);
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
    /// Selects the diagnostic identified by one Hex1b table row key.
    /// </summary>
    /// <param name="key">The stable diagnostic row key.</param>
    internal void SelectDiagnostic(object? key)
    {
        if (key is not string diagnosticKey ||
            !Snapshot.Diagnostics.Any(diagnostic =>
                GetDiagnosticKey(diagnostic) == diagnosticKey))
        {
            throw new InvalidOperationException(
                $"Unknown diagnostic row key: {key}.");
        }

        _focusedDiagnosticKey = diagnosticKey;
        SetFocusedRow(DashboardSection.Diagnostics, diagnosticKey);
    }

    /// <summary>
    /// Gets a stable table row key for one diagnostic.
    /// </summary>
    /// <param name="diagnostic">The diagnostic to identify.</param>
    /// <returns>The stable diagnostic row key.</returns>
    internal static string GetDiagnosticKey(ControlDiagnosticInfo diagnostic)
    {
        ArgumentNullException.ThrowIfNull(diagnostic);
        return string.Concat(
            diagnostic.ProjectName,
            "|",
            diagnostic.FilePath,
            "|",
            diagnostic.Line,
            "|",
            diagnostic.Character,
            "|",
            diagnostic.Id);
    }

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
            await LoadSnapshotAsync(
                processId,
                includeDiagnostics: Section == DashboardSection.Diagnostics).ConfigureAwait(false);
            OperationStatus = $"Completed {result.Operation}; generation " +
                $"{result.PreviousGeneration} -> {result.CurrentGeneration}; " +
                $"cleared {result.ClearedCacheEntryCount} cache entries.";
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
        if (!Snapshot.Diagnostics.Any(diagnostic =>
            GetDiagnosticKey(diagnostic) == _focusedDiagnosticKey))
        {
            _focusedDiagnosticKey = Snapshot.Diagnostics.Count != 0
                ? GetDiagnosticKey(Snapshot.Diagnostics[0])
                : null;
        }

        RefreshedAt = DateTimeOffset.UtcNow;
    }

    private async Task LoadDiagnosticsAsync(int processId)
    {
        try
        {
            await LoadSnapshotAsync(processId, includeDiagnostics: true).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_cancellationToken.IsCancellationRequested)
        {
            _diagnosticsLoadError = null;
        }
        catch (Exception exception) when (IsControlException(exception))
        {
            _diagnosticsLoadError = $"Diagnostics failed: {exception.Message}";
        }
        finally
        {
            _diagnosticsLoading = false;
            _app?.Invalidate();
        }
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
