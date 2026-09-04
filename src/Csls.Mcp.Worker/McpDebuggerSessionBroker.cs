using Csls.Debugger.Contracts;

namespace Csls.Mcp.Worker;

/// <summary>
/// Owns bounded debugger-worker sessions for one MCP connection.
/// </summary>
internal sealed partial class McpDebuggerSessionBroker : IAsyncDisposable
{
    private const int MaximumOwnedSessions = 8;
    private readonly Lock _gate = new();
    private readonly string? _dumpWorkerPath;
    private readonly string? _workerPath;
    private readonly Dictionary<string, McpDebuggerSession> _sessions =
        new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _sessionSlots = new(
        MaximumOwnedSessions,
        MaximumOwnedSessions);
    private bool _disposed;

    /// <summary>
    /// Signals authoritative resource changes for registered debugger sessions.
    /// </summary>
    internal event Action<McpDebuggerResourceChange>? ResourceChanged;

    /// <summary>
    /// Creates a debugger broker for the installed live and dump workers.
    /// </summary>
    /// <param name="workerPath">The optional absolute debugger worker path.</param>
    /// <param name="dumpWorkerPath">The optional absolute dump worker path.</param>
    internal McpDebuggerSessionBroker(string? workerPath, string? dumpWorkerPath = null)
    {
        _workerPath = string.IsNullOrWhiteSpace(workerPath)
            ? null
            : Path.GetFullPath(workerPath);
        _dumpWorkerPath = string.IsNullOrWhiteSpace(dumpWorkerPath)
            ? null
            : Path.GetFullPath(dumpWorkerPath);
    }

    /// <summary>
    /// Gets whether this installation can advertise debugger lifecycle tools.
    /// </summary>
    internal bool IsAvailable => HasLiveWorker || HasDumpWorker;

    /// <summary>
    /// Gets whether this installation can start or attach live targets.
    /// </summary>
    internal bool HasLiveWorker => _workerPath is not null;

    /// <summary>
    /// Gets whether this installation can inspect managed process dumps.
    /// </summary>
    internal bool HasDumpWorker => _dumpWorkerPath is not null;

    /// <summary>
    /// Tests whether this connection currently owns an exact debugger session.
    /// </summary>
    internal bool OwnsSession(string debugSession)
    {
        if (!Guid.TryParseExact(debugSession, "N", out _))
        {
            return false;
        }

        lock (_gate)
        {
            return !_disposed && _sessions.ContainsKey(debugSession);
        }
    }

    /// <summary>
    /// Launches one managed target through a newly supervised debugger worker.
    /// </summary>
    /// <param name="request">The validated managed launch request.</param>
    /// <param name="initialSourcePath">The optional initial breakpoint source.</param>
    /// <param name="initialLine">The optional one-based initial breakpoint line.</param>
    /// <param name="cancellationToken">The MCP request cancellation token.</param>
    /// <returns>The activated debugger-session projection.</returns>
    internal Task<McpDebugSessionInfo> LaunchAsync(
        DebugLaunchRequest request,
        string? initialSourcePath,
        int? initialLine,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return StartAsync(
            McpDebuggerSessionKind.Launch,
            _workerPath ?? throw new McpDebuggerException(
                "debugger_unavailable",
                "This MCP installation has no live debugger worker."),
            async (session, token) =>
            {
                if (initialSourcePath is not null)
                {
                    _ = await session.Client.SetSourceBreakpointsAsync(
                        new DebugSourceBreakpointSetRequest(
                            initialSourcePath,
                            [new DebugSourceBreakpointRequest(initialLine!.Value, null)]),
                        token).ConfigureAwait(false);
                }

                return await session.Client.LaunchAsync(request, token).ConfigureAwait(false);
            },
            cancellationToken);
    }

    /// <summary>
    /// Attaches one newly supervised debugger worker to an existing managed target.
    /// </summary>
    /// <param name="request">The validated managed attach request.</param>
    /// <param name="pause">Whether the target is paused after attachment.</param>
    /// <param name="cancellationToken">The MCP request cancellation token.</param>
    /// <returns>The activated debugger-session projection.</returns>
    internal Task<McpDebugSessionInfo> AttachAsync(
        DebugAttachRequest request,
        bool pause,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return StartAsync(
            McpDebuggerSessionKind.Attach,
            _workerPath ?? throw new McpDebuggerException(
                "debugger_unavailable",
                "This MCP installation has no live debugger worker."),
            async (session, token) =>
            {
                DebugSessionSnapshot snapshot = await session.Client.AttachAsync(request, token)
                    .ConfigureAwait(false);
                return pause
                    ? await session.Client.PauseAsync(token).ConfigureAwait(false)
                    : snapshot;
            },
            cancellationToken);
    }

    /// <summary>
    /// Opens one managed process dump through a newly supervised read-only worker.
    /// </summary>
    /// <param name="request">The validated dump-open request.</param>
    /// <param name="cancellationToken">The MCP request cancellation token.</param>
    /// <returns>The activated debugger-session projection.</returns>
    internal Task<McpDebugSessionInfo> OpenDumpAsync(
        DebugDumpOpenRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return StartAsync(
            McpDebuggerSessionKind.Dump,
            _dumpWorkerPath ?? throw new McpDebuggerException(
                "debugger_unavailable",
                "This MCP installation has no process-dump debugger worker."),
            async (session, token) => await session.Client.OpenDumpAsync(request, token)
                .ConfigureAwait(false),
            cancellationToken);
    }

    /// <summary>
    /// Gets one explicitly selected debugger session.
    /// </summary>
    /// <param name="debugSession">The exact opaque debugger-session identifier.</param>
    /// <param name="cancellationToken">The MCP request cancellation token.</param>
    /// <returns>The current debugger-session projection.</returns>
    internal Task<McpDebugSessionInfo> GetAsync(
        string debugSession,
        CancellationToken cancellationToken) =>
        Resolve(debugSession).GetInfoAsync(cancellationToken);

    /// <summary>
    /// Lists debugger sessions owned by this MCP connection.
    /// </summary>
    /// <param name="cancellationToken">The MCP request cancellation token.</param>
    /// <returns>The ordered current debugger-session projections.</returns>
    internal async Task<IReadOnlyList<McpDebugSessionInfo>> ListAsync(
        CancellationToken cancellationToken)
    {
        McpDebuggerSession[] sessions;
        lock (_gate)
        {
            ThrowIfDisposed();
            sessions = [.. _sessions.Values.OrderBy(static item => item.Id)];
        }

        var results = new List<McpDebugSessionInfo>(sessions.Length);
        foreach (McpDebuggerSession session in sessions)
        {
            results.Add(await session.GetInfoAsync(cancellationToken).ConfigureAwait(false));
        }

        return results;
    }

    /// <summary>
    /// Ends and releases one explicitly selected debugger session.
    /// </summary>
    /// <param name="debugSession">The exact opaque debugger-session identifier.</param>
    /// <param name="terminateAttachedTarget">Whether an attached process is explicitly terminated.</param>
    /// <param name="cancellationToken">The MCP request cancellation token.</param>
    /// <returns>The terminal debugger-session projection.</returns>
    internal async Task<McpDebugSessionInfo> EndAsync(
        string debugSession,
        bool terminateAttachedTarget,
        CancellationToken cancellationToken)
    {
        McpDebuggerSession session = RemoveForEnd(debugSession, terminateAttachedTarget);
        try
        {
            return await session.EndAsync(terminateAttachedTarget, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            await session.DisposeAsync().ConfigureAwait(false);
            _ = _sessionSlots.Release();
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        McpDebuggerSession[] sessions;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            sessions = [.. _sessions.Values];
            _sessions.Clear();
        }

        foreach (McpDebuggerSession session in sessions)
        {
            session.ResourceChanged -= OnResourceChanged;
        }

        await Task.WhenAll(sessions.Select(static item => item.DisposeAsync().AsTask()))
            .ConfigureAwait(false);
        _sessionSlots.Dispose();
    }

    private void OnResourceChanged(McpDebuggerResourceChange change) =>
        ResourceChanged?.Invoke(change);
}
