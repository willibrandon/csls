namespace Csls.TestProcessHost;

/// <summary>
/// Receives a runtime-validated derived reference through an explicit conversion operator.
/// </summary>
internal sealed class DebuggerExplicitReferenceInputDestination
{
    private readonly int _number;

    /// <summary>
    /// Initializes the explicit reference input destination.
    /// </summary>
    /// <param name="number">The converted number.</param>
    private DebuggerExplicitReferenceInputDestination(int number) => _number = number;

    /// <summary>
    /// Gets the converted number.
    /// </summary>
    internal int Number => _number;

    /// <summary>
    /// Converts a derived reference inside the target process.
    /// </summary>
    /// <param name="source">The runtime-validated derived reference.</param>
    /// <returns>The explicit conversion destination.</returns>
    public static explicit operator DebuggerExplicitReferenceInputDestination(
        DebuggerImplicitConversionReferenceDerived? source)
    {
        ArgumentNullException.ThrowIfNull(source);
        DebuggerImplicitConversionFixture.RecordConversion();
        return new DebuggerExplicitReferenceInputDestination(source.Number);
    }
}
