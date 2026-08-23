namespace Csls.Control.Contracts;

/// <summary>
/// Describes one queued or running request exposed by the control protocol.
/// </summary>
public sealed class ControlRequestInfo
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
    /// Gets whether peer or server cancellation was requested.
    /// </summary>
    public bool IsCancellationRequested { get; init; }

    /// <summary>
    /// Gets the current request lifecycle state.
    /// </summary>
    public required string Status { get; init; }
}
