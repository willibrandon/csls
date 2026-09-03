using Csls.Debugger.Contracts;
using Csls.Debugger.Control;
using System.Diagnostics;

namespace Csls.Mcp.Worker;

/// <summary>
/// Owns time-bounded target-control authorization for one MCP debugger session.
/// </summary>
internal sealed partial class McpDebuggerSession
{
    private readonly Lock _agentControlGate = new();
    private readonly Timer _agentControlExpirationTimer;
    private DateTimeOffset? _agentControlExpiresAtUtc;
    private TimeSpan _agentControlDuration;
    private long _agentControlGrantedTimestamp;

    /// <summary>
    /// Gets whether a time-bounded execution-control grant is currently active.
    /// </summary>
    internal bool AgentControl => GetAgentControlState().Enabled;

    /// <summary>
    /// Applies or revokes a time-bounded agent-control grant.
    /// </summary>
    /// <param name="enabled">Whether control should be granted.</param>
    /// <param name="duration">The positive grant duration when enabled.</param>
    internal void SetAgentControl(bool enabled, TimeSpan duration)
    {
        lock (_agentControlGate)
        {
            ClearAgentControl();
            if (enabled)
            {
                _agentControlGrantedTimestamp = Stopwatch.GetTimestamp();
                _agentControlDuration = duration;
                _agentControlExpiresAtUtc = DateTimeOffset.UtcNow.Add(duration);
                _ = _agentControlExpirationTimer.Change(duration, Timeout.InfiniteTimeSpan);
            }
        }

        PublishSessionChanged();
    }

    /// <summary>
    /// Invokes one serialized operation only while agent control remains active.
    /// </summary>
    /// <typeparam name="T">The operation result type.</typeparam>
    /// <param name="operation">The target-changing debugger operation.</param>
    /// <param name="cancellationToken">The MCP request cancellation token.</param>
    /// <returns>The operation result.</returns>
    internal Task<T> InvokeControlledAsync<T>(
        Func<DebuggerRpcClient, CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken) =>
        InvokeAsync(
            (client, token) =>
            {
                RequireAgentControl();
                return operation(client, token);
            },
            cancellationToken);

    /// <summary>
    /// Creates the public projection of an authoritative debugger snapshot.
    /// </summary>
    /// <param name="snapshot">The authoritative private debugger snapshot.</param>
    /// <returns>The public debugger-session projection.</returns>
    internal McpDebugSessionInfo CreateInfo(DebugSessionSnapshot snapshot)
    {
        (bool enabled, DateTimeOffset? expiresAtUtc) = GetAgentControlState();
        return McpDebugSessionInfo.Create(Id, Kind, enabled, expiresAtUtc, snapshot);
    }

    private (bool Enabled, DateTimeOffset? ExpiresAtUtc) GetAgentControlState()
    {
        bool expired = false;
        (bool Enabled, DateTimeOffset? ExpiresAtUtc) state;
        lock (_agentControlGate)
        {
            if (_agentControlExpiresAtUtc is null)
            {
                state = (false, null);
            }
            else if (Stopwatch.GetElapsedTime(_agentControlGrantedTimestamp) >=
                _agentControlDuration)
            {
                ClearAgentControl();
                expired = true;
                state = (false, null);
            }
            else
            {
                state = (true, _agentControlExpiresAtUtc);
            }
        }

        if (expired)
        {
            PublishSessionChanged();
        }

        return state;
    }

    private void RequireAgentControl()
    {
        if (!AgentControl)
        {
            throw new McpDebuggerException(
                "debugger_control_denied",
                $"Debugger session {Id} has no active agent-control grant.");
        }
    }

    private void ClearAgentControl()
    {
        _ = _agentControlExpirationTimer.Change(
            Timeout.InfiniteTimeSpan,
            Timeout.InfiniteTimeSpan);
        _agentControlExpiresAtUtc = null;
        _agentControlDuration = TimeSpan.Zero;
        _agentControlGrantedTimestamp = 0;
    }

    private void ExpireAgentControl()
    {
        lock (_agentControlGate)
        {
            if (_agentControlExpiresAtUtc is null)
            {
                return;
            }

            TimeSpan remaining = _agentControlDuration -
                Stopwatch.GetElapsedTime(_agentControlGrantedTimestamp);
            if (remaining > TimeSpan.Zero)
            {
                _ = _agentControlExpirationTimer.Change(
                    remaining,
                    Timeout.InfiniteTimeSpan);
                return;
            }

            ClearAgentControl();
        }

        PublishSessionChanged();
    }

    private void PublishSessionChanged() =>
        ResourceChanged?.Invoke(
            new McpDebuggerResourceChange(Id, DebuggerResourceChangeKind.Session));
}
