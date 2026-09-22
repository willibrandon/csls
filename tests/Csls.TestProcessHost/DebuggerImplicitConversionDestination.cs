namespace Csls.TestProcessHost;

/// <summary>
/// Receives an authored source through a target-executed implicit conversion operator.
/// </summary>
internal readonly struct DebuggerImplicitConversionDestination
{
    /// <summary>
    /// Initializes the authored destination value.
    /// </summary>
    /// <param name="number">The converted number.</param>
    internal DebuggerImplicitConversionDestination(int number) => Number = number;

    /// <summary>
    /// Gets the converted number.
    /// </summary>
    internal int Number { get; }

    /// <summary>
    /// Converts an authored source exactly once inside the target process.
    /// </summary>
    /// <param name="source">The source supplied to the conversion operator.</param>
    /// <returns>The converted destination value.</returns>
    public static implicit operator DebuggerImplicitConversionDestination(
        DebuggerImplicitConversionSource source)
    {
        DebuggerImplicitConversionFixture.RecordConversion();
        return new DebuggerImplicitConversionDestination(source.Number);
    }
}
