namespace Csls.TestProcessHost;

/// <summary>
/// Produces a primitive that requires a standard explicit result conversion.
/// </summary>
internal readonly struct DebuggerExplicitNumericResultSource
{
    /// <summary>
    /// Initializes the explicit numeric result source.
    /// </summary>
    /// <param name="number">The low-order result value.</param>
    internal DebuggerExplicitNumericResultSource(int number) => Number = number;

    /// <summary>
    /// Gets the low-order result value.
    /// </summary>
    internal int Number { get; }

    /// <summary>
    /// Converts the authored source to a wide primitive inside the target process.
    /// </summary>
    /// <param name="source">The authored source value.</param>
    /// <returns>A wide value whose low-order bits carry the source number.</returns>
    public static explicit operator long(DebuggerExplicitNumericResultSource source)
    {
        DebuggerImplicitConversionFixture.RecordConversion();
        return 0x1_0000_0000L + source.Number;
    }
}
