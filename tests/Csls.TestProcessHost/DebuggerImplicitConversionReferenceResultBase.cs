namespace Csls.TestProcessHost;

/// <summary>
/// Receives a derived operator result through an implicit reference conversion.
/// </summary>
internal class DebuggerImplicitConversionReferenceResultBase
{
    /// <summary>
    /// Initializes the authored reference result.
    /// </summary>
    /// <param name="number">The converted number.</param>
    internal DebuggerImplicitConversionReferenceResultBase(int number) => Number = number;

    /// <summary>
    /// Gets the converted number.
    /// </summary>
    internal int Number { get; }
}
