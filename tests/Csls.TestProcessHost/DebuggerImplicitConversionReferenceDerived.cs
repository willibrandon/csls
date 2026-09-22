namespace Csls.TestProcessHost;

/// <summary>
/// Supplies a derived reference to an operator declared by its loaded base type.
/// </summary>
internal sealed class DebuggerImplicitConversionReferenceDerived :
    DebuggerImplicitConversionReferenceSource
{
    /// <summary>
    /// Initializes the authored derived source.
    /// </summary>
    /// <param name="number">The number carried through conversion.</param>
    internal DebuggerImplicitConversionReferenceDerived(int number)
        : base(number)
    {
    }
}
