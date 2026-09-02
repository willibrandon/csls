using Csls.Client;
using Csls.Control;
using Csls.Control.Contracts;

namespace Csls.Mcp.Worker;

/// <summary>
/// Owns one reusable MCP connection to a live or transient csls session.
/// </summary>
internal sealed class McpSessionEntry
{
    private readonly TransientLanguageServerSession? _transientSession;
    private ControlSessionInfo? _session;
    private int _disposeState;
    private int _ownedSlotReleaseState;
    private int _sessionSlotReleaseState;

    /// <summary>
    /// Creates a reusable MCP session entry.
    /// </summary>
    /// <param name="socketPath">The absolute control-socket path.</param>
    /// <param name="workspaceReadiness">The optional workspace readiness operation.</param>
    /// <param name="transientSession">The optional transient language-server owner.</param>
    internal McpSessionEntry(
        string socketPath,
        Func<CancellationToken, Task>? workspaceReadiness,
        TransientLanguageServerSession? transientSession)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(socketPath);
        Client = new ControlRpcClient(
            socketPath,
            workspaceReadiness,
            retryRecoverableReads: false);
        _transientSession = transientSession;
    }

    /// <summary>
    /// Gets the reusable control client for this session.
    /// </summary>
    internal ControlRpcClient Client { get; }

    /// <summary>
    /// Gets the session identity observed when this entry connected.
    /// </summary>
    internal ControlSessionInfo Session => _session ?? throw new InvalidOperationException(
        "The MCP session entry has not connected.");

    /// <summary>
    /// Gets whether the MCP server owns the language-server process.
    /// </summary>
    internal bool OwnsSession => _transientSession is not null;

    /// <summary>
    /// Waits for the owned transient language-server process to exit.
    /// </summary>
    /// <param name="cancellationToken">The wait cancellation token.</param>
    /// <returns>A task that completes when the owned process exits.</returns>
    internal Task WaitForExitAsync(CancellationToken cancellationToken) =>
        _transientSession?.WaitForExitAsync(cancellationToken) ?? Task.CompletedTask;

    /// <summary>
    /// Records the identity returned by the connected control service.
    /// </summary>
    /// <param name="session">The connected session identity.</param>
    internal void SetSession(ControlSessionInfo session)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (Interlocked.CompareExchange(ref _session, session, comparand: null) is not null)
        {
            throw new InvalidOperationException("The MCP session identity is already set.");
        }
    }

    /// <summary>
    /// Claims responsibility for releasing this entry's total-session admission slot.
    /// </summary>
    /// <returns>True only for the caller responsible for releasing the slot.</returns>
    internal bool TryReleaseSessionSlot() =>
        Interlocked.Exchange(ref _sessionSlotReleaseState, 1) == 0;

    /// <summary>
    /// Claims responsibility for releasing this entry's owned-session admission slot.
    /// </summary>
    /// <returns>True only for an owned entry's responsible release caller.</returns>
    internal bool TryReleaseOwnedSlot() =>
        OwnsSession && Interlocked.Exchange(ref _ownedSlotReleaseState, 1) == 0;

    /// <summary>
    /// Releases the control connection and any MCP-owned language-server process.
    /// </summary>
    /// <returns>A task that completes after owned resources are released.</returns>
    internal async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposeState, 1) != 0)
        {
            return;
        }

        Task clientDisposal = Client.DisposeAsync().AsTask();
        Task transientDisposal = _transientSession?.DisposeAsync().AsTask() ??
            Task.CompletedTask;
        await Task.WhenAll(clientDisposal, transientDisposal).ConfigureAwait(false);
    }
}
