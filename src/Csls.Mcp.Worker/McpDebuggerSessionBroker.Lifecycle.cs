using Csls.Debugger.Contracts;
using System.Runtime.CompilerServices;

namespace Csls.Mcp.Worker;

/// <summary>
/// Creates, selects, and removes MCP-owned debugger sessions.
/// </summary>
internal sealed partial class McpDebuggerSessionBroker
{
    private async Task<McpDebugSessionInfo> StartAsync(
        McpDebuggerSessionKind kind,
        string workerPath,
        Func<McpDebuggerSession, CancellationToken, Task<DebugSessionSnapshot>> activation,
        CancellationToken cancellationToken)
    {
        if (!await _sessionSlots.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            throw new McpDebuggerException(
                "debugger_session_limit",
                $"This MCP connection already owns " +
                $"{MaximumOwnedSessions} debugger sessions.");
        }

        bool registered = false;
        try
        {
            lock (_gate)
            {
                ThrowIfDisposed();
            }

            string id = Guid.NewGuid().ToString("N");
            McpDebuggerSessionLease lease = await McpDebuggerSession.StartAsync(
                workerPath,
                id,
                kind,
                cancellationToken).ConfigureAwait(false);
            await using ConfiguredAsyncDisposable leaseScope = lease.ConfigureAwait(false);
            McpDebuggerSession session = lease.Session;
            DebugSessionSnapshot snapshot = await session.InvokeAsync(
                (_, token) => activation(session, token),
                cancellationToken).ConfigureAwait(false);
            lock (_gate)
            {
                ThrowIfDisposed();
                _sessions.Add(id, session);
                session.ResourceChanged += OnResourceChanged;
                lease.TransferOwnership();
                registered = true;
            }

            return session.CreateInfo(snapshot);
        }
        finally
        {
            if (!registered)
            {
                _ = _sessionSlots.Release();
            }
        }
    }

    private McpDebuggerSession Resolve(string debugSession)
    {
        ValidateSessionId(debugSession);
        lock (_gate)
        {
            ThrowIfDisposed();
            return _sessions.TryGetValue(debugSession, out McpDebuggerSession? session)
                ? session
                : throw new McpDebuggerException(
                    "debugger_session_not_found",
                    $"Debugger session {debugSession} does not exist.");
        }
    }

    private McpDebuggerSession RemoveForEnd(
        string debugSession,
        bool terminateAttachedTarget)
    {
        ValidateSessionId(debugSession);
        lock (_gate)
        {
            ThrowIfDisposed();
            if (!_sessions.TryGetValue(debugSession, out McpDebuggerSession? session))
            {
                throw new McpDebuggerException(
                    "debugger_session_not_found",
                    $"Debugger session {debugSession} does not exist.");
            }

            if (terminateAttachedTarget &&
                session.Kind == McpDebuggerSessionKind.Attach &&
                !session.AgentControl)
            {
                throw new McpDebuggerException(
                    "debugger_control_denied",
                    $"Debugger session {debugSession} " +
                    "has no active agent-control grant.");
            }

            _sessions.Remove(debugSession);
            session.ResourceChanged -= OnResourceChanged;
            return session;
        }
    }

    private static void ValidateSessionId(string debugSession)
    {
        if (string.IsNullOrWhiteSpace(debugSession) ||
            !Guid.TryParseExact(debugSession, "N", out _))
        {
            throw new McpDebuggerException(
                "debugger_session_invalid",
                "debugSession must be the opaque identifier " +
                "returned by a debugger lifecycle tool.");
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }
}
