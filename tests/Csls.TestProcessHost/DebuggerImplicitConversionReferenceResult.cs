namespace Csls.TestProcessHost;

/// <summary>
/// Provides a derived reference result for post-operator conversion.
/// </summary>
internal sealed class DebuggerImplicitConversionReferenceResult :
    DebuggerImplicitConversionReferenceResultBase
{
    /// <summary>
    /// Initializes the derived reference result.
    /// </summary>
    /// <param name="number">The converted number.</param>
    internal DebuggerImplicitConversionReferenceResult(int number)
        : base(number)
    {
    }
}
