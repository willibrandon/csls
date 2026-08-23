namespace Csls.Control.Contracts;

/// <summary>
/// Describes the active or most recently stopped bounded request trace.
/// </summary>
public sealed class ControlTraceInfo
{
    /// <summary>
    /// Gets whether request tracing is currently active.
    /// </summary>
    public bool IsActive { get; init; }

    /// <summary>
    /// Gets the active or most recent trace identifier.
    /// </summary>
    public Guid? TraceId { get; init; }

    /// <summary>
    /// Gets the time at which the trace started.
    /// </summary>
    public DateTimeOffset? StartedAt { get; init; }

    /// <summary>
    /// Gets the time at which the trace stopped.
    /// </summary>
    public DateTimeOffset? StoppedAt { get; init; }

    /// <summary>
    /// Gets the maximum number of retained trace entries.
    /// </summary>
    public int Capacity { get; init; }

    /// <summary>
    /// Gets the number of older trace entries omitted by capacity enforcement.
    /// </summary>
    public long DroppedEntries { get; init; }

    /// <summary>
    /// Gets the retained request lifecycle entries in receive order.
    /// </summary>
    public required IReadOnlyList<ControlTraceEntry> Entries { get; init; }
}
