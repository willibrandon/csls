namespace Csls.TestProcessHost;

/// <summary>
/// Receives a derived operator result through an implicit reference conversion.
/// </summary>
internal class DebuggerImplicitConversionReferenceResultBase
{
    private readonly int _number;

    /// <summary>
    /// Initializes the authored reference result.
    /// </summary>
    /// <param name="number">The converted number.</param>
    internal DebuggerImplicitConversionReferenceResultBase(int number) => _number = number;

    /// <summary>
    /// Gets the converted number.
    /// </summary>
    internal int Number => _number;

    /// <summary>
    /// Converts an authored source through a destination-base declaration.
    /// </summary>
    /// <param name="source">The authored source value.</param>
    /// <returns>A derived runtime value for nonnegative inputs and a base value otherwise.</returns>
    public static explicit operator DebuggerImplicitConversionReferenceResultBase(
        DebuggerImplicitConversionSource source)
    {
        DebuggerImplicitConversionFixture.RecordConversion();
        return source.Number >= 0
            ? new DebuggerExplicitReferenceDowncastResult(source.Number)
            : new DebuggerImplicitConversionReferenceResultBase(source.Number);
    }
}
