using Microsoft.Extensions.Logging;

namespace Csls.Control;

/// <summary>
/// Stores one immutable structured worker log entry in the bounded session buffer.
/// </summary>
internal sealed class ControlLogRecord
{
    /// <summary>
    /// Gets the monotonic session-local log sequence.
    /// </summary>
    internal long Sequence { get; init; }

    /// <summary>
    /// Gets the UTC time at which the entry was captured.
    /// </summary>
    internal DateTimeOffset Timestamp { get; init; }

    /// <summary>
    /// Gets the logging category name.
    /// </summary>
    internal required string Category { get; init; }

    /// <summary>
    /// Gets the logging level.
    /// </summary>
    internal LogLevel Level { get; init; }

    /// <summary>
    /// Gets the formatted log message.
    /// </summary>
    internal required string Message { get; init; }
}
