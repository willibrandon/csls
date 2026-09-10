using Csls.Debugger.Control;

namespace Csls.Mcp.Worker;

/// <summary>
/// Retains asynchronous ownership of a debugger session until registration succeeds.
/// </summary>
internal sealed class McpDebuggerSessionLease : IAsyncDisposable
{
    private McpDebuggerSession? _session;

    /// <summary>
    /// Creates an empty lease before its session is initialized.
    /// </summary>
    private McpDebuggerSessionLease()
    {
    }

    /// <summary>
    /// Creates a lease that directly owns a newly connected debugger session.
    /// </summary>
    /// <param name="id">The stable MCP session identifier.</param>
    /// <param name="kind">How the target will be acquired.</param>
    /// <param name="worker">The supervised debugger worker.</param>
    /// <returns>The initialized ownership lease.</returns>
    internal static McpDebuggerSessionLease Create(
        string id,
        McpDebuggerSessionKind kind,
        DebuggerWorkerProcess worker)
    {
        return new McpDebuggerSessionLease
        {
            _session = new McpDebuggerSession(
                id,
                kind,
                worker)
        };
    }

    /// <summary>
    /// Gets the session while the lease still owns it.
    /// </summary>
    internal McpDebuggerSession Session => _session
        ?? throw new InvalidOperationException("Debugger-session ownership was already transferred.");

    /// <summary>
    /// Transfers ownership to the debugger-session registry.
    /// </summary>
    internal void TransferOwnership()
    {
        _session = null;
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_session is not null)
        {
            await _session.DisposeAsync().ConfigureAwait(false);
            _session = null;
        }
    }
}
