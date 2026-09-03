using Csls.Debugger.Contracts;
using ModelContextProtocol;

namespace Csls.Mcp.Worker;

/// <summary>
/// Creates, selects, and removes MCP-owned debugger sessions.
/// </summary>
internal sealed partial class McpDebuggerSessionBroker
{
    private async Task<McpDebugSessionInfo> StartAsync(
        McpDebuggerSessionKind kind,
        bool agentControl,
        Func<McpDebuggerSession, CancellationToken, Task<DebugSessionSnapshot>> activation,
        CancellationToken cancellationToken)
    {
        if (!await _sessionSlots.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            throw new McpException(
                $"debugger_session_limit: This MCP connection already owns " +
                $"{MaximumOwnedSessions} debugger sessions.");
        }

        McpDebuggerSession? session = null;
        bool registered = false;
        try
        {
            lock (_gate)
            {
                ThrowIfDisposed();
            }

            string id = Guid.NewGuid().ToString("N");
            session = await McpDebuggerSession.StartAsync(
                _workerPath ?? throw new McpException(
                    "debugger_unavailable: This MCP installation has no debugger worker."),
                id,
                kind,
                agentControl,
                cancellationToken).ConfigureAwait(false);
            DebugSessionSnapshot snapshot = await activation(session, cancellationToken)
                .ConfigureAwait(false);
            lock (_gate)
            {
                ThrowIfDisposed();
                _sessions.Add(id, session);
                registered = true;
            }

            return McpDebugSessionInfo.Create(id, kind, agentControl, snapshot);
        }
        finally
        {
            if (!registered)
            {
                if (session is not null)
                {
                    await session.DisposeAsync().ConfigureAwait(false);
                }

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
                : throw new McpException(
                    $"debugger_session_not_found: Debugger session {debugSession} does not exist.");
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
                throw new McpException(
                    $"debugger_session_not_found: Debugger session {debugSession} does not exist.");
            }

            if (terminateAttachedTarget &&
                session.Kind == McpDebuggerSessionKind.Attach &&
                !session.AgentControl)
            {
                throw new McpException(
                    $"debugger_control_denied: Debugger session {debugSession} " +
                    "has no agent-control grant.");
            }

            _sessions.Remove(debugSession);
            return session;
        }
    }

    private static void ValidateSessionId(string debugSession)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(debugSession);
        if (!Guid.TryParseExact(debugSession, "N", out _))
        {
            throw new McpException(
                "debugger_session_invalid: debug_session must be the opaque identifier " +
                "returned by a debugger lifecycle tool.");
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }
}
