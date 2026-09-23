namespace Csls.TestProcessHost;

/// <summary>
/// Produces a base-declared reference for runtime-checked explicit result conversion.
/// </summary>
internal readonly struct DebuggerExplicitReferenceResultSource
{
    /// <summary>
    /// Initializes the explicit reference result source.
    /// </summary>
    /// <param name="number">The number controlling the runtime result type.</param>
    internal DebuggerExplicitReferenceResultSource(int number) => Number = number;

    /// <summary>
    /// Gets the number controlling the runtime result type.
    /// </summary>
    internal int Number { get; }

    /// <summary>
    /// Converts the source to a base-declared reference inside the target process.
    /// </summary>
    /// <param name="source">The authored conversion source.</param>
    /// <returns>A derived runtime value for nonnegative inputs and a base value otherwise.</returns>
    public static explicit operator DebuggerImplicitConversionReferenceResultBase(
        DebuggerExplicitReferenceResultSource source)
    {
        DebuggerImplicitConversionFixture.RecordConversion();
        return source.Number >= 0
            ? new DebuggerExplicitReferenceDowncastResult(source.Number)
            : new DebuggerImplicitConversionReferenceResultBase(source.Number);
    }
}
