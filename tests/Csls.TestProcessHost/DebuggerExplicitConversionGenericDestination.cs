namespace Csls.TestProcessHost;

/// <summary>
/// Receives a boxed value through a constructed explicit conversion parameter.
/// </summary>
/// <typeparam name="T">The loaded operator parameter type.</typeparam>
internal sealed class DebuggerExplicitConversionGenericDestination<T>
{
    private readonly int _number;

    /// <summary>
    /// Initializes the generic explicit conversion destination.
    /// </summary>
    /// <param name="number">The converted number.</param>
    private DebuggerExplicitConversionGenericDestination(int number) => _number = number;

    /// <summary>
    /// Gets the converted number.
    /// </summary>
    internal int Number => _number;

    /// <summary>
    /// Converts a constructed operator parameter to its destination.
    /// </summary>
    /// <param name="value">The boxed value supplied through the constructed parameter type.</param>
    /// <returns>The generic explicit conversion destination.</returns>
    public static explicit operator DebuggerExplicitConversionGenericDestination<T>(T value)
    {
        DebuggerImplicitConversionFixture.RecordConversion();
        return new DebuggerExplicitConversionGenericDestination<T>(
            value is int number ? number : -1);
    }
}
