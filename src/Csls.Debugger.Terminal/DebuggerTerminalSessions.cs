using Hex1b;
using System.Globalization;

namespace Csls.Debugger.Terminal;

/// <summary>
/// Owns a bounded set of independently controlled terminal debugger sessions.
/// </summary>
internal sealed class DebuggerTerminalSessions : IAsyncDisposable
{
    private const int MaximumSessions = 8;
    private readonly Lock _stateGate = new();
    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private readonly List<DebuggerTerminalOwnedSession> _sessions;
    private readonly Guid _initialSessionId;
    private Guid _selectedId;
    private DebuggerTerminalRefresh? _refresh;
    private Hex1bAppOptions? _appOptions;
    private string? _message;
    private long _version;
    private bool _disposed;
    private int _disposeStarted;

    /// <summary>
    /// Starts a collection with the initial target owned by the terminal.
    /// </summary>
    internal DebuggerTerminalSessions(DebuggerTerminalOwnedSession initial)
    {
        ArgumentNullException.ThrowIfNull(initial);
        _sessions = [initial];
        _initialSessionId = initial.Id;
        _selectedId = initial.Id;
    }

    /// <summary>
    /// Gets the exact session selected for the current terminal frame.
    /// </summary>
    internal DebuggerTerminalOwnedSession Selected
    {
        get
        {
            lock (_stateGate)
            {
                ThrowIfDisposed();
                return _sessions.Single(session => session.Id == _selectedId);
            }
        }
    }

    /// <summary>
    /// Gets the latest session-management diagnostic for the header.
    /// </summary>
    internal string? Message
    {
        get
        {
            lock (_stateGate)
            {
                return _message;
            }
        }
    }

    /// <summary>
    /// Connects every owned state and collection switch to one Hex1b workload.
    /// </summary>
    internal void AttachWorkload(Hex1bAppOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.WorkloadAdapter is not Hex1bAppWorkloadAdapter workload)
        {
            throw new InvalidOperationException("The debugger requires an application workload.");
        }

        lock (_stateGate)
        {
            ThrowIfDisposed();
            if (_appOptions is not null)
            {
                throw new InvalidOperationException("The terminal workload is already attached.");
            }

            _appOptions = options;
            _refresh = new DebuggerTerminalRefresh(workload);
            foreach (DebuggerTerminalOwnedSession session in _sessions)
            {
                session.State.AttachWorkload(options);
            }
        }
    }

    /// <summary>
    /// Captures browser rows and their selection version atomically.
    /// </summary>
    internal (long Version, IReadOnlyList<DebuggerTerminalSessionOption> Options) CaptureBrowser()
    {
        lock (_stateGate)
        {
            ThrowIfDisposed();
            DebuggerTerminalSessionOption[] options = [.. _sessions.Select(session =>
                new DebuggerTerminalSessionOption(session.Id,
                    $"{(session.Id == _selectedId ? "●" : " ")} " +
                    $"{session.DisplayName}  pid {session.State.Snapshot.ProcessId?.ToString(CultureInfo.InvariantCulture) ?? "-"}  " +
                    session.State.Snapshot.State))];
            return (_version, options);
        }
    }

    /// <summary>
    /// Switches only when a browser row still belongs to the captured collection version.
    /// </summary>
    internal async Task<bool> SelectAsync(
        Guid sessionId,
        long browserVersion,
        CancellationToken cancellationToken)
    {
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            bool selected;
            lock (_stateGate)
            {
                ThrowIfDisposed();
                DebuggerTerminalOwnedSession? candidate = _sessions.FirstOrDefault(
                    session => session.Id == sessionId);
                if (browserVersion == _version && candidate is not null)
                {
                    selected = true;
                    _selectedId = candidate.Id;
                    _version++;
                    _message = $"Selected {candidate.DisplayName}.";
                }
                else
                {
                    selected = false;
                    _message = "The session list changed. Reopen Sessions.";
                }
            }

            RequestRefresh();
            return selected;
        }
        finally
        {
            _ = _operationGate.Release();
        }
    }

    /// <summary>
    /// Runs an action only while its captured session remains selected.
    /// </summary>
    internal async Task InvokeSelectedAsync(
        Guid sessionId,
        Func<DebuggerTerminalState, Task> action,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(action);
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            DebuggerTerminalOwnedSession selected = Selected;
            if (selected.Id != sessionId)
            {
                ReportError("The selected session changed; retry the command.");
                return;
            }

            await action(selected.State).ConfigureAwait(false);
        }
        finally
        {
            _ = _operationGate.Release();
        }
    }

    /// <summary>
    /// Launches a new independent terminal-owned target and selects it.
    /// </summary>
    internal Task AddLaunchAsync(
        DebuggerTerminalLaunchOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        DebuggerTerminalHost.ValidateLaunch(options);
        return AddAsync(() => DebuggerTerminalOwnedSession.LaunchAsync(options, cancellationToken),
            cancellationToken);
    }

    /// <summary>
    /// Attaches a new independent terminal-owned target and selects it.
    /// </summary>
    internal Task AddAttachAsync(
        DebuggerTerminalAttachOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.ProcessId);
        return AddAsync(() => DebuggerTerminalOwnedSession.AttachAsync(options, cancellationToken),
            cancellationToken);
    }

    /// <summary>
    /// Publishes one actionable session-management error to the terminal.
    /// </summary>
    internal void ReportError(string message)
    {
        lock (_stateGate)
        {
            if (_disposed)
            {
                return;
            }

            _message = message;
        }

        RequestRefresh();
    }

    /// <summary>
    /// Rearms the collection's coalesced refresh before a terminal render.
    /// </summary>
    internal void AcknowledgeRefresh() => _refresh?.Acknowledge();

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposeStarted, 1) != 0)
        {
            return;
        }

        using SemaphoreSlim gateCleanup = _operationGate;
        await _operationGate.WaitAsync().ConfigureAwait(false);
        DebuggerTerminalOwnedSession[] sessions;
        try
        {
            lock (_stateGate)
            {
                _disposed = true;
                sessions = [.. _sessions.Where(session => session.Id != _initialSessionId)];
                _sessions.Clear();
            }

            await Task.WhenAll(sessions.Select(static session => session.DisposeAsync().AsTask()))
                .ConfigureAwait(false);
        }
        finally
        {
            _ = _operationGate.Release();
        }
    }

    private async Task AddAsync(
        Func<Task<DebuggerTerminalOwnedSession>> start,
        CancellationToken cancellationToken)
    {
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            lock (_stateGate)
            {
                ThrowIfDisposed();
                if (_sessions.Count >= MaximumSessions)
                {
                    throw new InvalidOperationException(
                        $"The terminal supports at most {MaximumSessions} owned sessions.");
                }
            }

            DebuggerTerminalOwnedSession session = await start().ConfigureAwait(false);
            try
            {
                lock (_stateGate)
                {
                    ThrowIfDisposed();
                    if (_appOptions is not null)
                    {
                        session.State.AttachWorkload(_appOptions);
                    }

                    _sessions.Add(session);
                    _selectedId = session.Id;
                    _version++;
                    _message = $"Selected {session.DisplayName}.";
                }
            }
            catch
            {
                await session.DisposeAsync().ConfigureAwait(false);
                throw;
            }

            RequestRefresh();
        }
        finally
        {
            _ = _operationGate.Release();
        }
    }

    private void RequestRefresh() => _refresh?.Request();

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }
}
