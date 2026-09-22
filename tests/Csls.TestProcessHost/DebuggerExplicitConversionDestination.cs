namespace Csls.TestProcessHost;

/// <summary>
/// Receives a widened primitive through an explicit conversion operator.
/// </summary>
internal sealed class DebuggerExplicitConversionDestination
{
    private readonly long _number;

    /// <summary>
    /// Initializes the explicit conversion destination.
    /// </summary>
    /// <param name="number">The converted number.</param>
    private DebuggerExplicitConversionDestination(long number) => _number = number;

    /// <summary>
    /// Gets the converted number.
    /// </summary>
    internal long Number => _number;

    /// <summary>
    /// Converts a widened primitive source inside the target process.
    /// </summary>
    /// <param name="number">The widened primitive supplied to the operator.</param>
    /// <returns>The explicit conversion destination.</returns>
    public static explicit operator DebuggerExplicitConversionDestination(long number)
    {
        DebuggerImplicitConversionFixture.RecordConversion();
        return new DebuggerExplicitConversionDestination(number);
    }
}
