namespace Csls.Control.Contracts;

/// <summary>
/// Identifies one live request to cancel through the control protocol.
/// </summary>
public sealed class ControlCancelRequest
{
    /// <summary>
    /// Gets the stable request correlation identifier.
    /// </summary>
    public Guid CorrelationId { get; init; }
}
