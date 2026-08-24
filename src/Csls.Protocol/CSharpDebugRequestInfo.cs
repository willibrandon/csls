namespace Csls.Protocol;

/// <summary>
/// Describes one queued or running language-server request.
/// </summary>
public sealed class CSharpDebugRequestInfo
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
    /// Gets the current request lifecycle phase.
    /// </summary>
    public required string Phase { get; init; }

    /// <summary>
    /// Gets the workspace generation captured when execution started.
    /// </summary>
    public long? WorkspaceGeneration { get; init; }

    /// <summary>
    /// Gets the elapsed queued or execution duration in milliseconds.
    /// </summary>
    public double DurationMs { get; init; }

    /// <summary>
    /// Gets whether peer or server cancellation was requested.
    /// </summary>
    public bool IsCancellationRequested { get; init; }
}
