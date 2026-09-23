namespace Csls.TestProcessHost;

/// <summary>
/// Receives exact authored value-type storage unboxed through an explicit conversion operator.
/// </summary>
internal sealed class DebuggerExplicitUnboxedStructInputDestination
{
    private readonly int _number;

    /// <summary>
    /// Initializes the explicit unboxed struct input destination.
    /// </summary>
    /// <param name="number">The number read from the authored value.</param>
    private DebuggerExplicitUnboxedStructInputDestination(int number) => _number = number;

    /// <summary>
    /// Gets the number read from the authored value.
    /// </summary>
    internal int Number => _number;

    /// <summary>
    /// Converts exact authored value-type storage inside the target process.
    /// </summary>
    /// <param name="value">The unboxed authored value.</param>
    /// <returns>The explicit conversion destination.</returns>
    public static explicit operator DebuggerExplicitUnboxedStructInputDestination(
        DebuggerOptionalStructFixture value)
    {
        DebuggerImplicitConversionFixture.RecordConversion();
        return new DebuggerExplicitUnboxedStructInputDestination(value.ReadNumber());
    }
}
