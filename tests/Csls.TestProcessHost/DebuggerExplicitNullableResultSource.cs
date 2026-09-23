namespace Csls.TestProcessHost;

/// <summary>
/// Produces a nullable result directly from an explicit conversion operator.
/// </summary>
internal readonly struct DebuggerExplicitNullableResultSource
{
    /// <summary>
    /// Initializes the nullable-result conversion source.
    /// </summary>
    /// <param name="number">The number controlling the nullable result.</param>
    internal DebuggerExplicitNullableResultSource(int number) => Number = number;

    /// <summary>
    /// Gets the number controlling the nullable result.
    /// </summary>
    internal int Number { get; }

    /// <summary>
    /// Converts the source directly to a nullable floating-point value.
    /// </summary>
    /// <param name="source">The authored conversion source.</param>
    /// <returns>The converted value, or null when the stored number is zero.</returns>
    public static explicit operator double?(DebuggerExplicitNullableResultSource source)
    {
        DebuggerImplicitConversionFixture.RecordConversion();
        return source.Number == 0 ? null : source.Number + 0.75;
    }
}
