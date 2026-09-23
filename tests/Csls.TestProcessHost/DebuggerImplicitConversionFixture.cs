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

    /// <summary>
    /// Runs the compiler's exact explicit conversion operator.
    /// </summary>
    /// <param name="source">The authored source value.</param>
    /// <returns>The compiler-produced floating-point result.</returns>
    internal static double CompilerExplicitDoubleForDebugger(
        DebuggerImplicitConversionSource source) => (double)source;

    /// <summary>
    /// Runs the compiler's lifted explicit conversion to a nullable reference.
    /// </summary>
    /// <param name="source">The nullable authored source value.</param>
    /// <returns>The compiler-produced nullable reference result.</returns>
    internal static string? CompilerExplicitLiftedStringForDebugger(
        DebuggerImplicitConversionSource? source) => (string?)source;

    /// <summary>
    /// Runs the compiler's lifted explicit conversion to a nullable value.
    /// </summary>
    /// <param name="source">The nullable authored source value.</param>
    /// <returns>The compiler-produced nullable value result.</returns>
    internal static double? CompilerExplicitLiftedDoubleForDebugger(
        DebuggerImplicitConversionSource? source) => (double?)source;

    /// <summary>
    /// Runs the compiler's explicit conversion followed by nullable result wrapping.
    /// </summary>
    /// <param name="source">The non-nullable authored source value.</param>
    /// <returns>The compiler-produced nullable value result.</returns>
    internal static double? CompilerExplicitNullableTargetForDebugger(
        DebuggerImplicitConversionSource source) => (double?)source;

    /// <summary>
    /// Runs the compiler's nullable source unwrapping before an explicit conversion.
    /// </summary>
    /// <param name="source">The nullable authored source value.</param>
    /// <returns>The compiler-produced non-nullable value result.</returns>
    internal static double CompilerExplicitNullableSourceForDebugger(
        DebuggerImplicitConversionSource? source)
    {
        if (!source.HasValue)
        {
            throw new InvalidOperationException("Nullable object must have a value.");
        }

        return (double)source.Value;
    }

    /// <summary>
    /// Runs the compiler's lifted explicit operator with a declared nullable result.
    /// </summary>
    /// <param name="source">The nullable authored source value.</param>
    /// <returns>The compiler-produced nullable value result.</returns>
    internal static double? CompilerExplicitNullableResultForDebugger(
        DebuggerExplicitNullableResultSource? source) => (double?)source;

    /// <summary>
    /// Runs the compiler's nullable numeric conversion before an explicit operator.
    /// </summary>
    /// <param name="source">The nullable numeric source value.</param>
    /// <returns>The number carried through the explicit conversion.</returns>
    internal static long CompilerExplicitNullableNumericSourceForDebugger(short? source)
    {
        var converted =
            (DebuggerExplicitConversionDestination?)source;
        return converted?.Number ?? -1;
    }

    /// <summary>
    /// Runs the compiler's numeric widening after an implicit operator selected by an explicit cast.
    /// </summary>
    /// <param name="source">The authored source value.</param>
    /// <returns>The widened operator result.</returns>
    internal static long CompilerExplicitLongForDebugger(
        DebuggerImplicitConversionSource source) => (long)source;

    /// <summary>
    /// Runs the compiler's numeric widening before an explicit conversion operator.
    /// </summary>
    /// <param name="source">The primitive source value.</param>
    /// <returns>The number carried through the explicit conversion.</returns>
    internal static long CompilerExplicitWidenedSourceForDebugger(byte source) =>
        ((DebuggerExplicitConversionDestination)source).Number;

    /// <summary>
    /// Runs the compiler's boxing conversion before a constructed explicit operator.
    /// </summary>
    /// <param name="source">The primitive source value.</param>
    /// <returns>The number carried through the explicit conversion.</returns>
    internal static int CompilerExplicitBoxedSourceForDebugger(int source) =>
        ((DebuggerExplicitConversionGenericDestination<IComparable>)source).Number;

    /// <summary>
    /// Runs the compiler's exact unboxing before an explicit conversion operator.
    /// </summary>
    /// <param name="source">The value-type-declared boxed primitive source.</param>
    /// <returns>The number carried through the explicit conversion.</returns>
    internal static int CompilerExplicitUnboxedInputForDebugger(ValueType source) =>
        ((DebuggerExplicitUnboxedInputDestination)source).Number;

    /// <summary>
    /// Runs the compiler's exact authored-struct unboxing before an explicit conversion operator.
    /// </summary>
    /// <param name="source">The value-type-declared boxed authored value source.</param>
    /// <returns>The number carried through the explicit conversion.</returns>
    internal static int CompilerExplicitUnboxedStructInputForDebugger(ValueType source) =>
        ((DebuggerExplicitUnboxedStructInputDestination)source).Number;

    /// <summary>
    /// Verifies that a direct built-in object cast takes precedence over user-defined conversion lookup.
    /// </summary>
    /// <param name="source">The object-declared boxed primitive source.</param>
    /// <returns>True when the compiler-selected built-in cast rejects the runtime value.</returns>
    internal static bool CompilerExplicitObjectInputUsesBuiltInForDebugger(object source)
    {
        try
        {
            _ = (DebuggerExplicitUnboxedInputDestination)source;
            return false;
        }
        catch (InvalidCastException)
        {
            return true;
        }
    }

    /// <summary>
    /// Runs the compiler's numeric narrowing before an explicit conversion operator.
    /// </summary>
    /// <param name="source">The primitive source value.</param>
    /// <returns>The number carried through the explicit conversion.</returns>
    internal static byte CompilerExplicitNarrowedSourceForDebugger(int source) =>
        ((DebuggerExplicitNarrowingDestination)source).Number;

    /// <summary>
    /// Runs the compiler's numeric narrowing after an explicit conversion operator.
    /// </summary>
    /// <param name="source">The authored conversion source.</param>
    /// <returns>The narrowed operator result.</returns>
    internal static int CompilerExplicitNarrowedResultForDebugger(
        DebuggerExplicitNumericResultSource source) => (int)source;

    /// <summary>
    /// Runs the compiler's numeric conversion before nullable result wrapping.
    /// </summary>
    /// <param name="source">The authored conversion source.</param>
    /// <returns>The nullable narrowed operator result.</returns>
    internal static int? CompilerExplicitNullableNumericResultForDebugger(
        DebuggerExplicitNumericResultSource source) => (int?)source;

    /// <summary>
    /// Runs the compiler's lifted operator before nullable numeric result conversion.
    /// </summary>
    /// <param name="source">The nullable authored conversion source.</param>
    /// <returns>The lifted nullable narrowed operator result.</returns>
    internal static int? CompilerExplicitLiftedNullableNumericResultForDebugger(
        DebuggerExplicitNumericResultSource? source) => (int?)source;

    /// <summary>
    /// Runs the compiler's lifted explicit numeric overload selection.
    /// </summary>
    /// <param name="source">The nullable numeric conversion source.</param>
    /// <returns>The selected overload marker and converted value.</returns>
    internal static long CompilerExplicitLiftedNumericOverloadForDebugger(short? source) =>
        ((DebuggerExplicitLiftedNumericOverloadDestination?)source)?.Number ?? -1;

    /// <summary>
    /// Runs the compiler's runtime-checked reference conversion before an explicit operator.
    /// </summary>
    /// <param name="source">The base-declared reference source.</param>
    /// <returns>The number carried through the explicit conversion.</returns>
    internal static int CompilerExplicitReferenceInputForDebugger(
        DebuggerImplicitConversionReferenceSource source) =>
        ((DebuggerExplicitReferenceInputDestination)
            (DebuggerImplicitConversionReferenceDerived)source).Number;

    /// <summary>
    /// Runs the compiler's runtime-checked reference conversion after an explicit operator.
    /// </summary>
    /// <param name="source">The authored conversion source.</param>
    /// <returns>The number carried through the derived conversion result.</returns>
    internal static int CompilerExplicitReferenceResultDowncastForDebugger(
        DebuggerExplicitReferenceResultSource source) =>
        ((DebuggerExplicitReferenceDowncastResult)source).Number;

    /// <summary>
    /// Runs the compiler's reference conversions around an inherited conversion operator.
    /// </summary>
    /// <param name="source">The derived reference source.</param>
    /// <returns>The number carried through the reference result.</returns>
    internal static int CompilerExplicitReferenceResultForDebugger(
        DebuggerImplicitConversionReferenceDerived source) =>
        ((DebuggerImplicitConversionReferenceResultBase)source).Number;

    /// <summary>
    /// Runs the compiler's explicit operator lookup through the destination base hierarchy.
    /// </summary>
    /// <param name="source">The authored conversion source.</param>
    /// <returns>The number carried through the derived conversion result.</returns>
    internal static int CompilerExplicitDestinationBaseForDebugger(
        DebuggerImplicitConversionSource source) =>
        ((DebuggerExplicitReferenceDowncastResult)source).Number;
}
