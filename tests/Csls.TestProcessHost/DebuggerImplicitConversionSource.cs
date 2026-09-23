namespace Csls.TestProcessHost;

/// <summary>
/// Carries an authored value into compiler and debugger implicit-conversion calls.
/// </summary>
internal readonly struct DebuggerImplicitConversionSource
{
    /// <summary>
    /// Initializes the authored source value.
    /// </summary>
    /// <param name="number">The number observed after conversion.</param>
    internal DebuggerImplicitConversionSource(int number) => Number = number;

    /// <summary>
    /// Gets the number observed after conversion.
    /// </summary>
    internal int Number { get; }

    /// <summary>
    /// Converts an authored source to its primitive number inside the target process.
    /// </summary>
    /// <param name="source">The authored source value.</param>
    /// <returns>The stored primitive number.</returns>
    public static implicit operator int(DebuggerImplicitConversionSource source)
    {
        DebuggerImplicitConversionFixture.RecordConversion();
        return source.Number;
    }

    /// <summary>
    /// Converts an authored source to a nullable reference inside the target process.
    /// </summary>
    /// <param name="source">The authored source value.</param>
    /// <returns>The invariant number text, or null for zero.</returns>
    public static implicit operator string?(DebuggerImplicitConversionSource source)
    {
        DebuggerImplicitConversionFixture.RecordConversion();
        return source.Number == 0
            ? null
            : source.Number < 0
                ? throw new InvalidOperationException("implicit conversion failed")
                : source.Number.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Explicitly converts an authored source to a floating-point value inside the target process.
    /// </summary>
    /// <param name="source">The authored source value.</param>
    /// <returns>The stored number plus one half.</returns>
    public static explicit operator double(DebuggerImplicitConversionSource source)
    {
        DebuggerImplicitConversionFixture.RecordConversion();
        return source.Number < 0
            ? throw new InvalidOperationException("explicit conversion failed")
            : source.Number + 0.5;
    }
}
