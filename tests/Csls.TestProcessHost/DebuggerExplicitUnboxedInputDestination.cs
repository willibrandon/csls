namespace Csls.TestProcessHost;

/// <summary>
/// Receives exact value storage unboxed through an explicit conversion operator.
/// </summary>
internal sealed class DebuggerExplicitUnboxedInputDestination
{
    private readonly int _number;

    /// <summary>
    /// Initializes the explicit unboxed input destination.
    /// </summary>
    /// <param name="number">The unboxed number.</param>
    private DebuggerExplicitUnboxedInputDestination(int number) => _number = number;

    /// <summary>
    /// Gets the unboxed number.
    /// </summary>
    internal int Number => _number;

    /// <summary>
    /// Converts exact integer storage inside the target process.
    /// </summary>
    /// <param name="number">The unboxed operator input.</param>
    /// <returns>The explicit conversion destination.</returns>
    public static explicit operator DebuggerExplicitUnboxedInputDestination(int number)
    {
        DebuggerImplicitConversionFixture.RecordConversion();
        return new DebuggerExplicitUnboxedInputDestination(number);
    }
}
