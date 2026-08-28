namespace Csls.Control.Contracts;

/// <summary>
/// Describes a live csls language-server session exposed through the control socket.
/// </summary>
public sealed class ControlSessionInfo
{
    /// <summary>
    /// Gets the operating-system process identifier of the session worker.
    /// </summary>
    public int ProcessId { get; init; }

    /// <summary>
    /// Gets the current language-server lifecycle state.
    /// </summary>
    public required string LifecycleState { get; init; }

    /// <summary>
    /// Gets the current workspace initialization phase.
    /// </summary>
    public string? WorkspacePhase { get; init; }

    /// <summary>
    /// Gets the immutable Roslyn workspace generation observed by the control request.
    /// </summary>
    public long WorkspaceGeneration { get; init; }

    /// <summary>
    /// Gets the absolute roots loaded into the current workspace snapshot.
    /// </summary>
    public required IReadOnlyList<string> WorkspaceRoots { get; init; }

    /// <summary>
    /// Gets the absolute Unix-domain-socket path for the session.
    /// </summary>
    public required string SocketPath { get; init; }
}
