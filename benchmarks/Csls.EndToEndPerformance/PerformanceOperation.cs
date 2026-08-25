namespace Csls.EndToEndPerformance;

/// <summary>
/// Describes one measured real-product operation.
/// </summary>
internal sealed class PerformanceOperation
{
    /// <summary>
    /// Gets the stable operation name.
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// Gets the elapsed operation time in milliseconds.
    /// </summary>
    public double Milliseconds { get; init; }
}
