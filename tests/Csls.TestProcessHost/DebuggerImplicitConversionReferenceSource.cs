namespace Csls.TestProcessHost;

/// <summary>
/// Declares an implicit conversion inherited by an authored reference source.
/// </summary>
internal class DebuggerImplicitConversionReferenceSource
{
    /// <summary>
    /// Initializes the authored reference source.
    /// </summary>
    /// <param name="number">The number carried through conversion.</param>
    internal DebuggerImplicitConversionReferenceSource(int number) => Number = number;

    /// <summary>
    /// Gets the number carried through conversion.
    /// </summary>
    internal int Number { get; }

    /// <summary>
    /// Converts a base-typed source to a derived reference result.
    /// </summary>
    /// <param name="source">The source accepted through an implicit reference conversion.</param>
    /// <returns>The derived reference result.</returns>
    public static implicit operator DebuggerImplicitConversionReferenceResult(
        DebuggerImplicitConversionReferenceSource source)
    {
        DebuggerImplicitConversionFixture.RecordConversion();
        return new DebuggerImplicitConversionReferenceResult(source.Number);
    }
}
