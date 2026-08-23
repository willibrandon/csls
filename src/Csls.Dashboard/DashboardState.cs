using Csls.Control;
using Csls.Control.Contracts;
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
    /// Creates state by discovering sessions and loading the requested live process.
    /// </summary>
    /// <param name="processId">The requested worker process, or zero to infer one.</param>
    /// <param name="cancellationToken">The dashboard cancellation token.</param>
    /// <returns>The initialized dashboard state.</returns>
    internal static async Task<DashboardState> CreateAsync(
        int processId,
        CancellationToken cancellationToken)
    {
        var state = new DashboardState(cancellationToken);
        await state.RefreshSessionsAsync(processId).ConfigureAwait(false);
        return state;
    }

    /// <summary>
    /// Refreshes discovery and the currently selected live session.
    /// </summary>
    /// <returns>A task that completes after real control RPC calls finish.</returns>
    internal Task RefreshAsync() => RefreshSessionsAsync(Snapshot.Session.ProcessId);

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

    private async Task RefreshSessionsAsync(int processId)
    {
        Sessions = await ControlSessionDiscovery
            .DiscoverAsync(_cancellationToken)
            .ConfigureAwait(false);
        int selectedProcessId = processId;
        if (selectedProcessId == 0)
        {
            selectedProcessId = Sessions.Count switch
            {
                0 => throw new InvalidOperationException(
                    "No live csls session was found. Start an editor session first."),
                1 => Sessions[0].ProcessId,
                _ => throw new InvalidOperationException(
                    "Multiple live csls sessions were found. Specify one with --session <pid>.")
            };
        }

        await SelectSessionAsync(selectedProcessId).ConfigureAwait(false);
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
}
