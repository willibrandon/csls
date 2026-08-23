namespace Csls.Control.Contracts;

/// <summary>
/// Describes one bounded structured worker log entry exposed by the control protocol.
/// </summary>
public sealed class ControlLogEntry
{
    /// <summary>
    /// Gets the monotonic session-local log sequence.
    /// </summary>
    public long Sequence { get; init; }

    /// <summary>
    /// Gets the UTC time at which the entry was captured.
    /// </summary>
    public DateTimeOffset Timestamp { get; init; }

    /// <summary>
    /// Gets the logging category name.
    /// </summary>
    public required string Category { get; init; }

    /// <summary>
    /// Gets the logging level name.
    /// </summary>
    public required string Level { get; init; }

    /// <summary>
    /// Gets the formatted log message.
    /// </summary>
    public required string Message { get; init; }
}
