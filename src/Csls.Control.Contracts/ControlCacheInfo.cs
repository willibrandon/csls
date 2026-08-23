namespace Csls.Control.Contracts;

/// <summary>
/// Describes one bounded session cache exposed by the control protocol.
/// </summary>
public sealed class ControlCacheInfo
{
    /// <summary>
    /// Gets the stable cache name.
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// Gets the number of retained cache entries.
    /// </summary>
    public int EntryCount { get; init; }

    /// <summary>
    /// Gets the configured maximum entry count when the cache has one.
    /// </summary>
    public int? Capacity { get; init; }
}
