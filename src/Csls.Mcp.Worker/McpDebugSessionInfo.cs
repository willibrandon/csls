using Csls.Debugger.Contracts;

namespace Csls.Mcp.Worker;

/// <summary>
/// Describes one explicitly selected MCP debugger session.
/// </summary>
internal sealed class McpDebugSessionInfo : IMcpDebugSessionResult
{
    /// <summary>
    /// Gets the stable opaque debugger-session identifier.
    /// </summary>
    public required string DebugSession { get; init; }

    /// <summary>
    /// Gets whether the session launched or attached to its target.
    /// </summary>
    public required string Mode { get; init; }

    /// <summary>
    /// Gets the current debugger lifecycle state.
    /// </summary>
    public required string State { get; init; }

    /// <summary>
    /// Gets whether this MCP connection may control target execution.
    /// </summary>
    public bool AgentControl { get; init; }

    /// <summary>
    /// Gets the target display name when available.
    /// </summary>
    public string? ProcessName { get; init; }

    /// <summary>
    /// Gets the target operating-system process identifier when available.
    /// </summary>
    public int? ProcessId { get; init; }

    /// <summary>
    /// Gets the current stop reason when stopped.
    /// </summary>
    public string? StopReason { get; init; }

    /// <summary>
    /// Gets the managed thread responsible for the current stop when known.
    /// </summary>
    public int? StoppedThreadId { get; init; }

    /// <summary>
    /// Gets the current stop generation, or zero before the first stop.
    /// </summary>
    public long StopGeneration { get; init; }

    /// <summary>
    /// Gets the target exit code after process exit.
    /// </summary>
    public int? ExitCode { get; init; }

    /// <summary>
    /// Creates the MCP projection of a private debugger snapshot.
    /// </summary>
    /// <param name="debugSession">The stable MCP session identifier.</param>
    /// <param name="kind">How the target was acquired.</param>
    /// <param name="agentControl">Whether control was explicitly granted.</param>
    /// <param name="snapshot">The private debugger snapshot.</param>
    /// <returns>The MCP session projection.</returns>
    internal static McpDebugSessionInfo Create(
        string debugSession,
        McpDebuggerSessionKind kind,
        bool agentControl,
        DebugSessionSnapshot snapshot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(debugSession);
        ArgumentNullException.ThrowIfNull(snapshot);
        return new McpDebugSessionInfo
        {
            DebugSession = debugSession,
            Mode = kind == McpDebuggerSessionKind.Launch ? "launch" : "attach",
            State = snapshot.State switch
            {
                DebugSessionState.Created => "created",
                DebugSessionState.Starting => "starting",
                DebugSessionState.Running => "running",
                DebugSessionState.Stopped => "stopped",
                DebugSessionState.Terminating => "terminating",
                DebugSessionState.Terminated => "terminated",
                DebugSessionState.Faulted => "faulted",
                _ => throw new InvalidDataException(
                    $"Unknown debugger session state {snapshot.State}.")
            },
            AgentControl = agentControl,
            ProcessName = snapshot.ProcessName,
            ProcessId = snapshot.ProcessId,
            StopReason = snapshot.StopReason,
            StoppedThreadId = snapshot.StoppedThreadId,
            StopGeneration = snapshot.StopGeneration,
            ExitCode = snapshot.ExitCode
        };
    }
}
