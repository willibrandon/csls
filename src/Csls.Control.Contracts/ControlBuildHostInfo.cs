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
    /// Gets the absolute workspace root served by the host.
    /// </summary>
    public required string WorkspaceRoot { get; init; }

    /// <summary>
    /// Gets the number of projects served by the host.
    /// </summary>
    public int ProjectCount { get; init; }
}
