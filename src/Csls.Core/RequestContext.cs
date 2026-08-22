namespace Csls.Core;

/// <summary>
/// Carries ordering, correlation, generation, and cancellation state for one request.
/// </summary>
public sealed class RequestContext
{
    /// <summary>
    /// Initializes request execution metadata.
    /// </summary>
    /// <param name="ordinal">The monotonically increasing receive ordinal.</param>
    /// <param name="correlationId">The request correlation identifier.</param>
    /// <param name="workspaceGeneration">The immutable workspace generation.</param>
    /// <param name="cancellationToken">The request cancellation token.</param>
    internal RequestContext(
        long ordinal,
        Guid correlationId,
        long workspaceGeneration,
        CancellationToken cancellationToken)
    {
        Ordinal = ordinal;
        CorrelationId = correlationId;
        WorkspaceGeneration = workspaceGeneration;
        CancellationToken = cancellationToken;
    }

    /// <summary>
    /// Gets the monotonically increasing receive ordinal.
    /// </summary>
    public long Ordinal { get; }

    /// <summary>
    /// Gets the request correlation identifier.
    /// </summary>
    public Guid CorrelationId { get; }

    /// <summary>
    /// Gets the immutable workspace generation observed by the request.
    /// </summary>
    public long WorkspaceGeneration { get; }

    /// <summary>
    /// Gets the token canceled by the peer, scheduler, or server shutdown.
    /// </summary>
    public CancellationToken CancellationToken { get; }
}
