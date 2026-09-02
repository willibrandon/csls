using Csls.Client;
using Csls.Control;
using Csls.Control.Contracts;
using ModelContextProtocol;
using StreamJsonRpc;
using System.Globalization;
using System.Net.Sockets;

namespace Csls.Mcp.Worker;

/// <summary>
/// Resolves typed MCP targets and reuses bounded connections across workspaces.
/// </summary>
internal sealed class McpSessionBroker : IAsyncDisposable
{
    private const int MaximumOwnedSessions = 32;
    private const int MaximumPathLength = 4096;
    private const int MaximumSessions = 256;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, Task<McpSessionEntry>> _entriesBySelector =
        new(CreateSelectorComparer());
    private readonly Dictionary<int, McpSessionEntry> _entriesByProcess = [];
    private readonly Dictionary<McpSessionEntry, HashSet<string>> _selectorsByEntry = [];
    private readonly HashSet<Task> _invocationTasks = [];
    private readonly HashSet<Task> _monitorTasks = [];
    private readonly CancellationTokenSource _lifetimeSource = new();
    private readonly SemaphoreSlim _ownedSessionSlots = new(
        MaximumOwnedSessions,
        MaximumOwnedSessions);
    private readonly SemaphoreSlim _sessionSlots = new(MaximumSessions, MaximumSessions);
    private bool _disposed;

    /// <summary>
    /// Invokes an operation against exactly one selected csls session.
    /// </summary>
    /// <typeparam name="T">The operation result type.</typeparam>
    /// <param name="workspace">The optional workspace, project, or document path.</param>
    /// <param name="session">The optional language-server process identifier.</param>
    /// <param name="socket">The optional absolute control-socket path.</param>
    /// <param name="operation">The control operation to invoke.</param>
    /// <param name="cancellationToken">The MCP request cancellation token.</param>
    /// <returns>The selected control operation result.</returns>
    internal Task<T> InvokeAsync<T>(
        string? workspace,
        int? session,
        string? socket,
        Func<ControlRpcClient, CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operation);
        return RunTrackedAsync(
            token => InvokeCoreAsync(workspace, session, socket, operation, token),
            cancellationToken);
    }

    /// <summary>
    /// Lists responsive discovered sessions and sessions connected through this MCP server.
    /// </summary>
    /// <param name="cancellationToken">The MCP request cancellation token.</param>
    /// <returns>The ordered responsive session list.</returns>
    internal Task<IReadOnlyList<ControlSessionInfo>> ListSessionsAsync(
        CancellationToken cancellationToken) =>
        RunTrackedAsync(ListSessionsCoreAsync, cancellationToken);

    private async Task<IReadOnlyList<ControlSessionInfo>> ListSessionsCoreAsync(
        CancellationToken cancellationToken)
    {
        IReadOnlyList<ControlSessionInfo> discovered;
        try
        {
            discovered = await ControlSessionDiscovery.DiscoverAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (IsExpectedSelectionFailure(exception))
        {
            throw new McpException($"csls session discovery failed: {exception.Message}");
        }

        McpSessionEntry[] connectedEntries;
        lock (_gate)
        {
            ThrowIfDisposed();
            connectedEntries = [.. _entriesByProcess.Values];
        }

        var sessions = discovered.ToDictionary(static item => item.ProcessId);
        foreach (McpSessionEntry entry in connectedEntries)
        {
            try
            {
                ControlSessionInfo refreshed = await entry.Client
                    .GetSessionAsync(cancellationToken)
                    .ConfigureAwait(false);
                sessions[refreshed.ProcessId] = refreshed;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception) when (IsConnectionFailure(exception))
            {
                await EvictAsync(entry).ConfigureAwait(false);
            }
        }

        return [.. sessions.Values.OrderBy(static item => item.ProcessId)];
    }

    /// <summary>
    /// Releases all cached connections and every MCP-owned transient session.
    /// </summary>
    /// <returns>A task that completes after all broker resources are released.</returns>
    public async ValueTask DisposeAsync()
    {
        Task<McpSessionEntry>[] acquisitions;
        Task[] invocations;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            acquisitions = [.. _entriesBySelector.Values.Distinct()];
            invocations = [.. _invocationTasks];
        }

        await _lifetimeSource.CancelAsync().ConfigureAwait(false);
        await Task.WhenAll(invocations).ConfigureAwait(
            ConfigureAwaitOptions.SuppressThrowing);
        Task acquisitionCompletion = Task.WhenAll(acquisitions);
        await acquisitionCompletion.ConfigureAwait(
            ConfigureAwaitOptions.SuppressThrowing);

        McpSessionEntry[] entries;
        Task[] monitors;
        lock (_gate)
        {
            entries = [.. _entriesByProcess.Values];
            monitors = [.. _monitorTasks];
            _entriesBySelector.Clear();
            _entriesByProcess.Clear();
            _selectorsByEntry.Clear();
        }

        var entryDisposal = Task.WhenAll(entries.Select(DisposeEntryAsync));
        await entryDisposal.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);

        await Task.WhenAll(monitors).ConfigureAwait(
            ConfigureAwaitOptions.SuppressThrowing);
        _ownedSessionSlots.Dispose();
        _sessionSlots.Dispose();
        _lifetimeSource.Dispose();
        if (entryDisposal.IsFaulted)
        {
            await entryDisposal.ConfigureAwait(false);
        }
    }

    private async Task<T> InvokeCoreAsync<T>(
        string? workspace,
        int? session,
        string? socket,
        Func<ControlRpcClient, CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken)
    {
        McpSessionEntry entry = await GetSessionAsync(
            workspace,
            session,
            socket,
            cancellationToken).ConfigureAwait(false);
        try
        {
            return await operation(entry.Client, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (IsConnectionFailure(exception))
        {
            await EvictAsync(entry).ConfigureAwait(false);
            throw new McpException(
                $"The selected csls session disconnected: {exception.Message}");
        }
    }

    private Task<T> RunTrackedAsync<T>(
        Func<CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken)
    {
        var completion = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate)
        {
            ThrowIfDisposed();
            _invocationTasks.Add(completion.Task);
        }

        return RunTrackedCoreAsync(operation, completion, cancellationToken);
    }

    private async Task<T> RunTrackedCoreAsync<T>(
        Func<CancellationToken, Task<T>> operation,
        TaskCompletionSource completion,
        CancellationToken cancellationToken)
    {
        using var requestSource = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _lifetimeSource.Token);
        try
        {
            return await operation(requestSource.Token).ConfigureAwait(false);
        }
        finally
        {
            lock (_gate)
            {
                _invocationTasks.Remove(completion.Task);
            }

            completion.TrySetResult();
        }
    }

    private async Task<McpSessionEntry> GetSessionAsync(
        string? workspace,
        int? session,
        string? socket,
        CancellationToken cancellationToken)
    {
        string selector = CreateSelector(workspace, session, socket);
        Task<McpSessionEntry> acquisition;
        lock (_gate)
        {
            ThrowIfDisposed();
            if (!_entriesBySelector.TryGetValue(selector, out acquisition!))
            {
                acquisition = CreateAndRegisterAsync(
                    selector,
                    workspace,
                    session,
                    socket,
                    _lifetimeSource.Token);
                _entriesBySelector.Add(selector, acquisition);
            }
        }

        try
        {
            return await acquisition.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (McpException)
        {
            RemoveFailedAcquisition(selector, acquisition);
            throw;
        }
        catch (Exception exception) when (IsExpectedSelectionFailure(exception))
        {
            RemoveFailedAcquisition(selector, acquisition);
            throw new McpException($"The csls target could not be selected: {exception.Message}");
        }
    }

    private async Task<McpSessionEntry> CreateAndRegisterAsync(
        string selector,
        string? workspace,
        int? session,
        string? socket,
        CancellationToken cancellationToken)
    {
        if (!await _sessionSlots.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            throw new McpException(
                $"The csls MCP server reached its session limit of {MaximumSessions}.");
        }

        McpSessionEntry? candidate = null;
        bool transferred = false;
        try
        {
            candidate = workspace is not null
                ? await CreateWorkspaceEntryAsync(workspace, cancellationToken)
                    .ConfigureAwait(false)
                : session.HasValue
                    ? await CreateProcessEntryAsync(session.Value, cancellationToken)
                        .ConfigureAwait(false)
                    : await CreateSocketEntryAsync(socket!, cancellationToken).ConfigureAwait(false);
            McpSessionEntry selected;
            bool monitorSelected = false;
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (_entriesByProcess.TryGetValue(candidate.Session.ProcessId, out selected!))
                {
                    AddSelector(selected, selector);
                }
                else
                {
                    selected = candidate;
                    transferred = true;
                    _entriesByProcess.Add(selected.Session.ProcessId, selected);
                    _selectorsByEntry.Add(selected, []);
                    AddSelector(selected, selector);
                    AddSelector(selected, CreateProcessSelector(selected.Session.ProcessId));
                    AddSelector(selected, CreateSocketSelector(selected.Session.SocketPath));
                    if (selected.OwnsSession)
                    {
                        monitorSelected = true;
                    }
                }
            }

            if (monitorSelected)
            {
                TrackMonitor(selected);
            }

            return selected;
        }
        finally
        {
            if (!transferred)
            {
                if (candidate is not null)
                {
                    await candidate.DisposeAsync().ConfigureAwait(false);
                    ReleaseOwnedSlot(candidate);
                }

                _sessionSlots.Release();
            }
        }
    }

    private async Task<McpSessionEntry> CreateWorkspaceEntryAsync(
        string workspace,
        CancellationToken cancellationToken)
    {
        ControlSessionInfo? liveSession = await ControlSessionDiscovery
            .TryResolveWorkspaceAsync(workspace, cancellationToken)
            .ConfigureAwait(false);
        if (liveSession is not null)
        {
            return await CreateConnectedEntryAsync(
                liveSession.SocketPath,
                expectedProcessId: liveSession.ProcessId,
                transientSession: null,
                workspaceReadiness: null,
                cancellationToken).ConfigureAwait(false);
        }

        if (!await _ownedSessionSlots.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            throw new McpException(
                "The csls MCP server reached its owned transient-session limit " +
                $"of {MaximumOwnedSessions}.");
        }

        TransientLanguageServerSession? transient = null;
        try
        {
            transient = await TransientLanguageServerSession.StartInitializingAsync(
                workspace,
                "csls-mcp",
                cancellationToken).ConfigureAwait(false);
            return await CreateConnectedEntryAsync(
                ControlEndpoint.GetSocketPath(transient.ProcessId),
                transient.ProcessId,
                transient,
                transient.WaitUntilReadyAsync,
                cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            if (transient is not null)
            {
                await transient.DisposeAsync().ConfigureAwait(false);
            }

            _ownedSessionSlots.Release();
            throw;
        }
    }

    private static Task<McpSessionEntry> CreateProcessEntryAsync(
        int processId,
        CancellationToken cancellationToken) =>
        CreateConnectedEntryAsync(
            ControlEndpoint.GetSocketPath(processId),
            processId,
            transientSession: null,
            workspaceReadiness: null,
            cancellationToken);

    private static Task<McpSessionEntry> CreateSocketEntryAsync(
        string socketPath,
        CancellationToken cancellationToken) =>
        CreateConnectedEntryAsync(
            socketPath,
            expectedProcessId: null,
            transientSession: null,
            workspaceReadiness: null,
            cancellationToken);

    private static async Task<McpSessionEntry> CreateConnectedEntryAsync(
        string socketPath,
        int? expectedProcessId,
        TransientLanguageServerSession? transientSession,
        Func<CancellationToken, Task>? workspaceReadiness,
        CancellationToken cancellationToken)
    {
        var entry = new McpSessionEntry(
            socketPath,
            workspaceReadiness,
            transientSession);
        try
        {
            ControlSessionInfo session = await entry.Client.GetSessionAsync(cancellationToken)
                .ConfigureAwait(false);
            if (expectedProcessId.HasValue && session.ProcessId != expectedProcessId.Value)
            {
                throw new InvalidDataException(
                    $"Session {expectedProcessId.Value} identified itself as process {session.ProcessId}.");
            }

            entry.SetSession(session);
            if (!PathsEqual(session.SocketPath, socketPath))
            {
                throw new InvalidDataException(
                    $"Session {session.ProcessId} reported socket {session.SocketPath} " +
                    $"instead of {Path.GetFullPath(socketPath)}.");
            }

            return entry;
        }
        catch
        {
            await entry.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private static string CreateSelector(string? workspace, int? session, string? socket)
    {
        int selectorCount = (workspace is null ? 0 : 1) +
            (session.HasValue ? 1 : 0) +
            (socket is null ? 0 : 1);
        if (selectorCount != 1)
        {
            throw new McpException(
                "Specify exactly one target: workspace, session, or socket.");
        }

        if (workspace is not null)
        {
            if (string.IsNullOrWhiteSpace(workspace) || workspace.Length > MaximumPathLength)
            {
                throw new McpException(
                    $"workspace must contain between 1 and {MaximumPathLength} characters.");
            }

            string fullPath = Path.GetFullPath(workspace);
            if (!Directory.Exists(fullPath) && !File.Exists(fullPath))
            {
                throw new McpException($"The csls workspace does not exist: {fullPath}.");
            }

            return CreateWorkspaceSelector(fullPath);
        }

        if (session.HasValue)
        {
            if (session.Value <= 0)
            {
                throw new McpException("session must be a positive process identifier.");
            }

            return CreateProcessSelector(session.Value);
        }

        if (string.IsNullOrWhiteSpace(socket) || socket.Length > MaximumPathLength)
        {
            throw new McpException(
                $"socket must contain between 1 and {MaximumPathLength} characters.");
        }

        if (!Path.IsPathFullyQualified(socket))
        {
            throw new McpException("socket must be an absolute path.");
        }

        return CreateSocketSelector(socket);
    }

    private static string CreateWorkspaceSelector(string workspace) =>
        $"workspace:{Path.GetFullPath(workspace)}";

    private static string CreateProcessSelector(int processId) =>
        $"session:{processId.ToString(CultureInfo.InvariantCulture)}";

    private static string CreateSocketSelector(string socketPath) =>
        $"socket:{Path.GetFullPath(socketPath)}";

    private void AddSelector(McpSessionEntry entry, string selector)
    {
        _entriesBySelector[selector] = Task.FromResult(entry);
        _selectorsByEntry[entry].Add(selector);
    }

    private void RemoveFailedAcquisition(
        string selector,
        Task<McpSessionEntry> acquisition)
    {
        lock (_gate)
        {
            if (_entriesBySelector.TryGetValue(selector, out Task<McpSessionEntry>? current) &&
                ReferenceEquals(current, acquisition))
            {
                _entriesBySelector.Remove(selector);
            }
        }
    }

    private async Task EvictAsync(McpSessionEntry entry)
    {
        lock (_gate)
        {
            if (!_entriesByProcess.Remove(entry.Session.ProcessId))
            {
                return;
            }

            if (_selectorsByEntry.Remove(entry, out HashSet<string>? selectors))
            {
                foreach (string selector in selectors)
                {
                    _entriesBySelector.Remove(selector);
                }
            }

        }

        await entry.DisposeAsync().ConfigureAwait(false);
        ReleaseSlots(entry);
    }

    private async Task DisposeEntryAsync(McpSessionEntry entry)
    {
        try
        {
            await entry.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            ReleaseSlots(entry);
        }
    }

    private void ReleaseSlots(McpSessionEntry entry)
    {
        ReleaseOwnedSlot(entry);
        if (entry.TryReleaseSessionSlot())
        {
            _sessionSlots.Release();
        }
    }

    private void ReleaseOwnedSlot(McpSessionEntry entry)
    {
        if (entry.TryReleaseOwnedSlot())
        {
            _ownedSessionSlots.Release();
        }
    }

    private void TrackMonitor(McpSessionEntry entry)
    {
        Task monitor = MonitorOwnedSessionAsync(entry);
        lock (_gate)
        {
            _monitorTasks.Add(monitor);
        }

        _ = monitor.ContinueWith(
            completed =>
            {
                lock (_gate)
                {
                    _monitorTasks.Remove(completed);
                }
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private async Task MonitorOwnedSessionAsync(McpSessionEntry entry)
    {
        try
        {
            await entry.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            await EvictAsync(entry).ConfigureAwait(false);
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or ObjectDisposedException)
        {
            return;
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    private static bool IsConnectionFailure(Exception exception) =>
        exception is IOException or SocketException or ConnectionLostException or
            ObjectDisposedException;

    private static bool IsExpectedSelectionFailure(Exception exception) =>
        exception is ArgumentException or FileNotFoundException or IOException or
            InvalidDataException or InvalidOperationException or SocketException or
            TimeoutException or ConnectionLostException or ObjectDisposedException ||
        exception is OperationCanceledException;

    private static bool PathsEqual(string left, string right) =>
        string.Equals(
            Path.GetFullPath(left),
            Path.GetFullPath(right),
            OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal);

    private static StringComparer CreateSelectorComparer() =>
        OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;
}
