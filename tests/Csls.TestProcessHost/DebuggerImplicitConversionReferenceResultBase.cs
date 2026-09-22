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
}
