namespace Csls.TestProcessHost;

/// <summary>
/// Exposes compiler-authored and debugger-authored implicit conversion calls.
/// </summary>
internal static class DebuggerImplicitConversionFixture
{
    private static int s_conversionCount;

    /// <summary>
    /// Records one target-executed implicit conversion.
    /// </summary>
    internal static void RecordConversion() => s_conversionCount++;

    /// <summary>
    /// Gets the number of target-executed implicit conversions.
    /// </summary>
    /// <returns>The conversion count.</returns>
    internal static int GetConversionCountForDebugger() => s_conversionCount;

    /// <summary>
    /// Selects the authored destination overload over an object fallback.
    /// </summary>
    /// <param name="value">The converted authored value.</param>
    /// <returns>The converted number plus one hundred.</returns>
    internal static int SelectForDebugger(DebuggerImplicitConversionDestination value) =>
        value.Number + 100;

    /// <summary>
    /// Provides the standard boxing fallback for overload selection.
    /// </summary>
    /// <param name="value">The boxed source value.</param>
    /// <returns>The fallback marker.</returns>
    internal static int SelectForDebugger(object value)
    {
        GC.KeepAlive(value);
        return -1;
    }

    /// <summary>
    /// Runs the compiler-selected implicit conversion for debugger comparison.
    /// </summary>
    /// <param name="source">The authored source value.</param>
    /// <returns>The compiler-selected overload result.</returns>
    internal static int CompilerSelectForDebugger(DebuggerImplicitConversionSource source) =>
        SelectForDebugger(source);

    /// <summary>
    /// Reads a primitive produced by an implicit conversion operator.
    /// </summary>
    /// <param name="value">The converted primitive value.</param>
    /// <returns>The primitive value plus two hundred.</returns>
    internal static int RequireIntegerForDebugger(int value) => value + 200;

    /// <summary>
    /// Reads a nullable reference produced by an implicit conversion operator.
    /// </summary>
    /// <param name="value">The converted nullable reference.</param>
    /// <returns>The text length, or minus one for null.</returns>
    internal static int RequireStringForDebugger(string? value) => value?.Length ?? -1;
}
