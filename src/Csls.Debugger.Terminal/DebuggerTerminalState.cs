using Csls.Debugger.Contracts;
using Csls.Debugger.Control;
using Hex1b;
using StreamJsonRpc;

namespace Csls.Debugger.Terminal;

/// <summary>
/// Holds immutable debugger snapshots loaded exclusively through private control RPC.
/// </summary>
internal sealed partial class DebuggerTerminalState : IAsyncDisposable
{
    private readonly DebuggerRpcClient _client;
    private readonly CancellationToken _cancellationToken;
    private readonly Lock _notificationGate = new();
    private readonly SemaphoreSlim _mutationGate = new(1, 1);
    private Hex1bApp? _app;
    private CancellationTokenSource? _runObservationCancellation;
    private Task? _runObservationTask;
    private TaskCompletionSource _sessionChanged = CreateSessionChangedSource();

    private DebuggerTerminalState(
        DebuggerRpcClient client,
        CancellationToken cancellationToken)
    {
        _client = client;
        _cancellationToken = cancellationToken;
        _client.ResourceChanged += OnResourceChanged;
    }

    /// <summary>
    /// Gets the current debugger lifecycle snapshot.
    /// </summary>
    internal DebugSessionSnapshot Snapshot { get; private set; } =
        new() { State = DebugSessionState.Created };

    /// <summary>
    /// Gets the latest non-fatal interactive operation diagnostic.
    /// </summary>
    internal string? StatusMessage { get; private set; }

    /// <summary>
    /// Creates state and waits until the target reaches its initial stop.
    /// </summary>
    /// <param name="client">The connected debugger RPC client.</param>
    /// <param name="cancellationToken">The interactive session cancellation token.</param>
    /// <returns>The fully loaded stopped-state snapshot.</returns>
    internal static async Task<DebuggerTerminalState> CreateAsync(
        DebuggerRpcClient client,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(client);
        var state = new DebuggerTerminalState(client, cancellationToken);
        await state.WaitForStopAndLoadAsync(cancellationToken).ConfigureAwait(false);
        return state;
    }

    /// <summary>
    /// Attaches the running Hex1b application used to redraw state changes.
    /// </summary>
    /// <param name="app">The running application.</param>
    internal void AttachApp(Hex1bApp app)
    {
        ArgumentNullException.ThrowIfNull(app);
        _app = app;
    }

    /// <summary>
    /// Stops background observation and releases its owned cancellation state.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        _client.ResourceChanged -= OnResourceChanged;
        await StopRunObservationAsync().ConfigureAwait(false);
        _mutationGate.Dispose();
    }

    private void StartRunObservation()
    {
        _runObservationCancellation?.Dispose();
        _runObservationCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            _cancellationToken);
        _runObservationTask = ObserveRunAsync(_runObservationCancellation.Token);
    }

    private async Task StopRunObservationAsync()
    {
        CancellationTokenSource? cancellation = _runObservationCancellation;
        Task? observation = _runObservationTask;
        _runObservationCancellation = null;
        _runObservationTask = null;
        if (cancellation is null)
        {
            return;
        }

        await cancellation.CancelAsync().ConfigureAwait(false);
        if (observation is not null)
        {
            await observation.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }

        cancellation.Dispose();
    }

    private async Task ObserveRunAsync(CancellationToken cancellationToken)
    {
        try
        {
            await WaitForStopAndLoadAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception) when (exception is
            IOException or
            InvalidDataException or
            InvalidOperationException or
            ObjectDisposedException or
            RemoteInvocationException)
        {
            await _mutationGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
            try
            {
                StatusMessage = exception.Message;
                _app?.Invalidate();
            }
            finally
            {
                _ = _mutationGate.Release();
            }
        }
    }

    private async Task WaitForStopAndLoadAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            Task sessionChanged;
            lock (_notificationGate)
            {
                sessionChanged = _sessionChanged.Task;
            }

            DebugSessionSnapshot snapshot = await _client.GetSessionAsync(cancellationToken)
                .ConfigureAwait(false);
            if (snapshot.State is DebugSessionState.Stopped or
                DebugSessionState.Terminated or
                DebugSessionState.Faulted)
            {
                await _mutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    Snapshot = snapshot;
                    if (snapshot.State == DebugSessionState.Stopped)
                    {
                        await LoadStoppedStateAsync(cancellationToken).ConfigureAwait(false);
                    }
                    else
                    {
                        ClearInspection();
                        _app?.Invalidate();
                    }

                    return;
                }
                finally
                {
                    _ = _mutationGate.Release();
                }
            }

            await sessionChanged.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private void OnResourceChanged(
        object? sender,
        DebuggerResourceChangeEventArgs change)
    {
        _ = sender;
        if ((change.Kind & DebuggerResourceChangeKind.Session) == 0)
        {
            return;
        }

        TaskCompletionSource changed;
        lock (_notificationGate)
        {
            changed = _sessionChanged;
            _sessionChanged = CreateSessionChangedSource();
        }

        changed.TrySetResult();
    }

    private static TaskCompletionSource CreateSessionChangedSource() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);
}
