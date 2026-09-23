namespace Csls.TestProcessHost;

/// <summary>
/// Retains a private generic base field for captured storage identity checks.
/// </summary>
/// <typeparam name="T">The constructed field type.</typeparam>
internal class DebuggerDumpFieldBase<T>
{
    private readonly T _value;

    /// <summary>
    /// Initializes the private base field.
    /// </summary>
    /// <param name="value">The retained value.</param>
    protected DebuggerDumpFieldBase(T value) => _value = value;

    /// <summary>
    /// Returns the base field through ordinary target code.
    /// </summary>
    /// <returns>The retained base value.</returns>
    internal T GetBaseValue() => _value;
}
