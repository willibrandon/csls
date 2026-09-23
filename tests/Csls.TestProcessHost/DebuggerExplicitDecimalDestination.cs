namespace Csls.TestProcessHost;

/// <summary>
/// Exposes a decimal operator for standard-conversion boundary coverage.
/// </summary>
internal sealed class DebuggerExplicitDecimalDestination
{
    /// <summary>
    /// Initializes the decimal destination.
    /// </summary>
    /// <param name="number">The converted decimal number.</param>
    private DebuggerExplicitDecimalDestination(decimal number) => Number = number;

    /// <summary>
    /// Gets the converted decimal number.
    /// </summary>
    internal decimal Number { get; }

    /// <summary>
    /// Converts an exact decimal source inside the target process.
    /// </summary>
    /// <param name="number">The decimal supplied to the operator.</param>
    /// <returns>The explicit decimal destination.</returns>
    public static explicit operator DebuggerExplicitDecimalDestination(decimal number)
    {
        DebuggerImplicitConversionFixture.RecordConversion();
        return new DebuggerExplicitDecimalDestination(number);
    }
}
