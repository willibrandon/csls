namespace Csls.Control.Contracts;

/// <summary>
/// Describes the result of one request cancellation attempt.
/// </summary>
public sealed class ControlCancelRequestResult
{
    /// <summary>
    /// Gets the requested correlation identifier.
    /// </summary>
    public Guid CorrelationId { get; init; }

    /// <summary>
    /// Gets whether cancellation was delivered to a live request.
    /// </summary>
    public bool CancellationRequested { get; init; }
}
