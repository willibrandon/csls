namespace Csls.Control.Contracts;

/// <summary>
/// Describes one Roslyn workspace host exposed by the control protocol.
/// </summary>
public sealed class ControlBuildHostInfo
{
    /// <summary>
    /// Gets the host process identifier.
    /// </summary>
    public int ProcessId { get; init; }

    /// <summary>
    /// Gets the Roslyn workspace implementation name.
    /// </summary>
    public required string Kind { get; init; }

    /// <summary>
    /// Gets the number of Roslyn workspaces served by the host process.
    /// </summary>
    public int WorkspaceCount { get; init; }

    /// <summary>
    /// Gets the number of projects served by the host.
    /// </summary>
    public int ProjectCount { get; init; }
}
