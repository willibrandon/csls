namespace Csls.Control.Contracts;

/// <summary>
/// Describes one bounded request lifecycle record in a control trace.
/// </summary>
public sealed class ControlTraceEntry
{
    /// <summary>
    /// Gets the monotonically increasing receive ordinal.
    /// </summary>
    public long Ordinal { get; init; }

    /// <summary>
    /// Gets the stable request correlation identifier.
    /// </summary>
    public Guid CorrelationId { get; init; }

    /// <summary>
    /// Gets the protocol or control operation name.
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// Gets the scheduler concurrency mode.
    /// </summary>
    public required string Mode { get; init; }

    /// <summary>
    /// Gets the workspace generation captured when execution started.
    /// </summary>
    public long? WorkspaceGeneration { get; init; }

    /// <summary>
    /// Gets the time at which the scheduler received the request.
    /// </summary>
    public DateTimeOffset AcceptedAt { get; init; }

    /// <summary>
    /// Gets the time at which request execution started.
    /// </summary>
    public DateTimeOffset? StartedAt { get; init; }

    /// <summary>
    /// Gets the time at which request execution completed.
    /// </summary>
    public DateTimeOffset? CompletedAt { get; init; }

    /// <summary>
    /// Gets the elapsed execution time in milliseconds.
    /// </summary>
    public double DurationMilliseconds { get; init; }

    /// <summary>
    /// Gets whether peer or server cancellation was requested.
    /// </summary>
    public bool IsCancellationRequested { get; init; }

    /// <summary>
    /// Gets the current or final request lifecycle state.
    /// </summary>
    public required string Status { get; init; }

    /// <summary>
    /// Gets the unqualified exception type when the request failed.
    /// </summary>
    public string? ExceptionType { get; init; }

    /// <summary>
    /// Gets the bounded exception message when the request failed.
    /// </summary>
    public string? ExceptionMessage { get; init; }
}
