namespace Csls.TestProcessHost;

/// <summary>
/// Distinguishes lifted explicit conversion overloads by their numeric parameter type.
/// </summary>
internal sealed class DebuggerExplicitLiftedNumericOverloadDestination
{
    private readonly long _number;

    /// <summary>
    /// Initializes a lifted numeric overload destination.
    /// </summary>
    /// <param name="number">The overload marker and converted value.</param>
    private DebuggerExplicitLiftedNumericOverloadDestination(long number) => _number = number;

    /// <summary>
    /// Gets the overload marker and converted value.
    /// </summary>
    internal long Number => _number;

    /// <summary>
    /// Converts through the nearest lifted numeric parameter.
    /// </summary>
    /// <param name="number">The converted integer value.</param>
    /// <returns>The integer-overload destination.</returns>
    public static explicit operator DebuggerExplicitLiftedNumericOverloadDestination(int number)
    {
        DebuggerImplicitConversionFixture.RecordConversion();
        return new DebuggerExplicitLiftedNumericOverloadDestination(number + 1000);
    }

    /// <summary>
    /// Converts through the wider lifted numeric parameter.
    /// </summary>
    /// <param name="number">The converted long value.</param>
    /// <returns>The long-overload destination.</returns>
    public static explicit operator DebuggerExplicitLiftedNumericOverloadDestination(long number)
    {
        DebuggerImplicitConversionFixture.RecordConversion();
        return new DebuggerExplicitLiftedNumericOverloadDestination(number + 2000);
    }
}
