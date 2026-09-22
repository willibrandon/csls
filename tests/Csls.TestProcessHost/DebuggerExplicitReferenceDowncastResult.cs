namespace Csls.TestProcessHost;

/// <summary>
/// Supplies a derived runtime result through a base-declared conversion operator return.
/// </summary>
internal sealed class DebuggerExplicitReferenceDowncastResult :
    DebuggerImplicitConversionReferenceResultBase
{
    /// <summary>
    /// Initializes the derived explicit conversion result.
    /// </summary>
    /// <param name="number">The converted number.</param>
    internal DebuggerExplicitReferenceDowncastResult(int number)
        : base(number)
    {
    }
}
