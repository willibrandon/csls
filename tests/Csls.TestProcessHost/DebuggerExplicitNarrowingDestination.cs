namespace Csls.TestProcessHost;

/// <summary>
/// Receives a narrowed primitive through an explicit conversion operator.
/// </summary>
internal sealed class DebuggerExplicitNarrowingDestination
{
    private readonly byte _number;

    /// <summary>
    /// Initializes the explicit narrowing destination.
    /// </summary>
    /// <param name="number">The narrowed number.</param>
    private DebuggerExplicitNarrowingDestination(byte number) => _number = number;

    /// <summary>
    /// Gets the narrowed number.
    /// </summary>
    internal byte Number => _number;

    /// <summary>
    /// Converts a narrowed primitive source inside the target process.
    /// </summary>
    /// <param name="number">The narrowed primitive supplied to the operator.</param>
    /// <returns>The explicit conversion destination.</returns>
    public static explicit operator DebuggerExplicitNarrowingDestination(byte number)
    {
        DebuggerImplicitConversionFixture.RecordConversion();
        return new DebuggerExplicitNarrowingDestination(number);
    }
}
