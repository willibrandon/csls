namespace Csls.Cli.Worker;

/// <summary>
/// Describes one named workspace doctor observation.
/// </summary>
internal sealed class DoctorCheck
{
    /// <summary>
    /// Gets the stable check name.
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// Gets the observed check status.
    /// </summary>
    public DoctorCheckStatus Status { get; init; }

    /// <summary>
    /// Gets the concise observation or corrective action.
    /// </summary>
    public required string Message { get; init; }
}
