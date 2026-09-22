namespace Csls.TestProcessHost;

/// <summary>
/// Receives a standard conversion through a constructed generic operator parameter.
/// </summary>
/// <typeparam name="T">The loaded operator parameter type.</typeparam>
internal sealed class DebuggerImplicitConversionGenericDestination<T>
{
    /// <summary>
    /// Initializes the generic conversion destination.
    /// </summary>
    /// <param name="number">The converted number.</param>
    private DebuggerImplicitConversionGenericDestination(int number) => Number = number;

    /// <summary>
    /// Gets the converted number.
    /// </summary>
    internal int Number { get; }

    /// <summary>
    /// Converts a constructed operator parameter to its destination.
    /// </summary>
    /// <param name="value">The value converted through the constructed parameter type.</param>
    /// <returns>The generic destination.</returns>
    public static implicit operator DebuggerImplicitConversionGenericDestination<T>(T value)
    {
        DebuggerImplicitConversionFixture.RecordConversion();
        return new DebuggerImplicitConversionGenericDestination<T>(
            value is int number ? number : -1);
    }
}
