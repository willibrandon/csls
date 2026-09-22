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

    /// <summary>
    /// Reads a widened primitive produced by an implicit conversion operator.
    /// </summary>
    /// <param name="value">The widened operator result.</param>
    /// <returns>The widened value plus three hundred.</returns>
    internal static long RequireLongForDebugger(long value) => value + 300;

    /// <summary>
    /// Runs the compiler's numeric conversion before an implicit operator.
    /// </summary>
    /// <returns>The compiler-selected destination overload result.</returns>
    internal static int CompilerSelectByteForDebugger() => SelectForDebugger((byte)41);

    /// <summary>
    /// Runs the compiler's numeric conversion after an implicit operator.
    /// </summary>
    /// <param name="source">The authored source value.</param>
    /// <returns>The compiler-produced widened result.</returns>
    internal static long CompilerRequireLongForDebugger(
        DebuggerImplicitConversionSource source) => RequireLongForDebugger(source);

    /// <summary>
    /// Reads a derived operator result through its loaded base declaration.
    /// </summary>
    /// <param name="value">The reference-converted operator result.</param>
    /// <returns>The converted number plus three hundred.</returns>
    internal static int RequireReferenceBaseForDebugger(
        DebuggerImplicitConversionReferenceResultBase value) => value.Number + 300;

    /// <summary>
    /// Runs the compiler's standard conversions around an inherited implicit operator.
    /// </summary>
    /// <param name="source">The derived authored source.</param>
    /// <returns>The compiler-produced reference conversion result.</returns>
    internal static int CompilerRequireReferenceBaseForDebugger(
        DebuggerImplicitConversionReferenceDerived source) =>
        RequireReferenceBaseForDebugger(source);

    /// <summary>
    /// Reads a destination whose constructed operator parameter receives a boxed source.
    /// </summary>
    /// <param name="value">The generic conversion destination.</param>
    /// <returns>The converted number plus four hundred.</returns>
    internal static int RequireBoxedSourceForDebugger(
        DebuggerImplicitConversionGenericDestination<IComparable> value) =>
        value.Number + 400;

    /// <summary>
    /// Runs the compiler's boxing conversion before a constructed implicit operator.
    /// </summary>
    /// <param name="source">The primitive source value.</param>
    /// <returns>The compiler-produced generic destination result.</returns>
    internal static int CompilerRequireBoxedSourceForDebugger(int source) =>
        RequireBoxedSourceForDebugger(source);

    /// <summary>
    /// Reads a lifted nullable value-type conversion result.
    /// </summary>
    /// <param name="value">The lifted nullable destination.</param>
    /// <returns>The converted number plus five hundred, or minus one for null.</returns>
    internal static int RequireNullableDestinationForDebugger(
        DebuggerImplicitConversionDestination? value) =>
        value is { } present ? present.Number + 500 : -1;

    /// <summary>
    /// Runs the compiler's lifted nullable value-type conversion.
    /// </summary>
    /// <param name="source">The nullable authored source value.</param>
    /// <returns>The compiler-produced nullable destination result.</returns>
    internal static int CompilerRequireNullableDestinationForDebugger(
        DebuggerImplicitConversionSource? source) =>
        RequireNullableDestinationForDebugger(source);

    /// <summary>
    /// Runs the compiler's lifted nullable reference conversion.
    /// </summary>
    /// <param name="source">The nullable authored source value.</param>
    /// <returns>The compiler-produced reference result length.</returns>
    internal static int CompilerRequireNullableStringForDebugger(
        DebuggerImplicitConversionSource? source) => RequireStringForDebugger(source);
}
