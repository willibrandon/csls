using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace Csls.TestProcessHost;

/// <summary>
/// Retains distinct CLR array layouts while an independent process captures its heap.
/// </summary>
internal static class DebuggerDumpArrayFixture
{
    /// <summary>
    /// Ends the target while a debugger-owned function evaluation is running.
    /// </summary>
    internal static void ExitDuringDebuggerEvaluation()
    {
        Console.Error.WriteLine("csls-evaluation-exit-entered");
        Console.Error.Flush();
        Environment.Exit(37);
    }

    /// <summary>
    /// Kills the target without a managed evaluation or process-exit callback.
    /// </summary>
    internal static void CrashDuringDebuggerEvaluation()
    {
        Console.Error.WriteLine("csls-evaluation-crash-entered");
        Console.Error.Flush();
        using var process = Process.GetCurrentProcess();
        process.Kill();
    }

    /// <summary>
    /// Selects the array's generic collection interface over System.Object.
    /// </summary>
    /// <param name="value">The array supplied through debugger evaluation.</param>
    /// <returns>The interface overload marker.</returns>
    internal static int SelectArrayReferenceForDebugger(IReadOnlyList<int> value)
    {
        GC.KeepAlive(value);
        return 17;
    }

    /// <summary>
    /// Selects the object overload for an explicitly object-typed argument.
    /// </summary>
    /// <param name="value">The reference supplied through debugger evaluation.</param>
    /// <returns>The object overload marker.</returns>
    internal static int SelectArrayReferenceForDebugger(object value)
    {
        GC.KeepAlive(value);
        return 23;
    }

    /// <summary>
    /// Exposes a parameter incompatible with the fixture's array argument.
    /// </summary>
    /// <param name="value">A disposable reference.</param>
    /// <returns>The unrelated overload marker.</returns>
    internal static int RequireDisposableForDebugger(IDisposable value)
    {
        GC.KeepAlive(value);
        return 31;
    }

    /// <summary>
    /// Reads a primitive after the target boxes it for an object parameter.
    /// </summary>
    /// <param name="value">The boxed primitive value.</param>
    /// <returns>The boxed integer plus one hundred.</returns>
    internal static int ReadBoxedIntegerForDebugger(object value) =>
        value is int number ? number + 100 : -1;

    /// <summary>
    /// Reads an authored struct after the target boxes it for an object parameter.
    /// </summary>
    /// <param name="value">The boxed authored value type.</param>
    /// <returns>The authored struct's stored number.</returns>
    internal static int ReadBoxedStructForDebugger(object value) =>
        value is DebuggerOptionalStructFixture fixture ? fixture.ReadNumber() : -1;

    /// <summary>
    /// Reads a nullable integer after the target applies its boxing conversion.
    /// </summary>
    /// <param name="value">The nullable boxing result.</param>
    /// <returns>The contained integer plus one hundred, or a marker for null or another type.</returns>
    internal static int ReadBoxedNullableForDebugger(object? value) =>
        value is int number ? number + 100 : value is null ? -1 : -2;

    /// <summary>
    /// Runs the compiler's populated nullable boxing conversion for comparison with debugger evaluation.
    /// </summary>
    /// <returns>The target-observed nullable boxing result.</returns>
    internal static int CompilerReadBoxedNullableValueForDebugger()
    {
        int? value = 47;
        return ReadBoxedNullableForDebugger(value);
    }

    /// <summary>
    /// Runs the compiler's empty nullable boxing conversion for comparison with debugger evaluation.
    /// </summary>
    /// <returns>The target-observed nullable boxing result.</returns>
    internal static int CompilerReadBoxedNullableEmptyForDebugger()
    {
        int? value = null;
        return ReadBoxedNullableForDebugger(value);
    }

    /// <summary>
    /// Selects an implemented interface over object for a boxed integer.
    /// </summary>
    /// <param name="value">The boxed comparable value.</param>
    /// <returns>The interface overload marker.</returns>
    internal static int SelectBoxedIntegerForDebugger(IComparable value)
    {
        GC.KeepAlive(value);
        return 37;
    }

    /// <summary>
    /// Provides the object fallback for boxed integer overload selection.
    /// </summary>
    /// <param name="value">The boxed object value.</param>
    /// <returns>The object overload marker.</returns>
    internal static int SelectBoxedIntegerForDebugger(object value)
    {
        GC.KeepAlive(value);
        return 39;
    }

    /// <summary>
    /// Runs the compiler-selected boxing overload for comparison with debugger binding.
    /// </summary>
    /// <returns>The compiler-selected overload marker.</returns>
    internal static int CompilerSelectBoxedIntegerForDebugger() =>
        SelectBoxedIntegerForDebugger(41);

    /// <summary>
    /// Runs the compiler-selected nullable boxing overload for comparison with debugger binding.
    /// </summary>
    /// <returns>The compiler-selected interface overload marker.</returns>
    internal static int CompilerSelectBoxedNullableIntegerForDebugger() =>
        SelectBoxedIntegerForDebugger(((int?)41)!);

    /// <summary>
    /// Selects System.ValueType over object for a boxed integer.
    /// </summary>
    /// <param name="value">The boxed value-type instance.</param>
    /// <returns>The value-type overload marker.</returns>
    internal static int SelectBoxedValueTargetForDebugger(ValueType value)
    {
        GC.KeepAlive(value);
        return 43;
    }

    /// <summary>
    /// Provides the object fallback for boxed value-type overload selection.
    /// </summary>
    /// <param name="value">The boxed object value.</param>
    /// <returns>The object overload marker.</returns>
    internal static int SelectBoxedValueTargetForDebugger(object value)
    {
        GC.KeepAlive(value);
        return 47;
    }

    /// <summary>
    /// Runs the compiler-selected value-type overload for debugger comparison.
    /// </summary>
    /// <returns>The compiler-selected overload marker.</returns>
    internal static int CompilerSelectBoxedValueTargetForDebugger() =>
        SelectBoxedValueTargetForDebugger(41);

    /// <summary>
    /// Runs the compiler-selected nullable value-type overload for debugger comparison.
    /// </summary>
    /// <returns>The compiler-selected value-type overload marker.</returns>
    internal static int CompilerSelectBoxedNullableValueTargetForDebugger() =>
        SelectBoxedValueTargetForDebugger(((int?)41)!);

    /// <summary>
    /// Marks the long overload selected after widening an integer argument.
    /// </summary>
    /// <param name="value">The widened argument.</param>
    /// <returns>The long overload marker and its received value.</returns>
    internal static long SelectWidenedForDebugger(long value) => value + 1000;

    /// <summary>
    /// Marks the double overload selected by an exact floating-point argument.
    /// </summary>
    /// <param name="value">The floating-point argument.</param>
    /// <returns>The double overload marker and its received value.</returns>
    internal static double SelectWidenedForDebugger(double value) => value + 2000;

    /// <summary>
    /// Marks the decimal overload selected by an exact decimal argument.
    /// </summary>
    /// <param name="value">The decimal argument.</param>
    /// <returns>The decimal overload marker when its scale is zero.</returns>
    internal static int SelectWidenedForDebugger(decimal value) =>
        decimal.GetBits(value) is [41, 0, 0, 0] ? 3041 : 0;

    /// <summary>
    /// Gets the compiler-selected overload for an integer argument.
    /// </summary>
    /// <returns>The compiled overload marker.</returns>
    internal static long CompilerSelectWidenedForDebugger() => SelectWidenedForDebugger(41);

    /// <summary>
    /// Requires an unsigned argument to reject signed-to-unsigned call conversions.
    /// </summary>
    /// <param name="value">The unsigned argument.</param>
    /// <returns>The received value.</returns>
    internal static uint RequireUnsignedForDebugger(uint value) => value;

    /// <summary>
    /// Marks the signed overload preferred for a byte argument.
    /// </summary>
    /// <param name="value">The widened signed argument.</param>
    /// <returns>The signed overload marker.</returns>
    internal static int SelectSignedForDebugger(int value) => value + 100;

    /// <summary>
    /// Marks the unsigned overload competing for a byte argument.
    /// </summary>
    /// <param name="value">The widened unsigned argument.</param>
    /// <returns>The unsigned overload marker.</returns>
    internal static int SelectSignedForDebugger(uint value) => checked((int)value) + 200;

    /// <summary>
    /// Gets the compiler-selected signed overload for the same byte argument.
    /// </summary>
    /// <returns>The compiler-selected overload marker.</returns>
    internal static int CompilerSelectSignedForDebugger()
    {
        byte value = 41;
        return SelectSignedForDebugger(value);
    }

    /// <summary>
    /// Receives an integer constant narrowed to the byte parameter by C# overload resolution.
    /// </summary>
    /// <param name="value">The converted constant.</param>
    /// <returns>The byte overload marker and its received value.</returns>
    internal static int RequireByteForDebugger(byte value) => value + 7000;

    /// <summary>
    /// Gets the compiler's result for an integer constant passed to a byte parameter.
    /// </summary>
    /// <returns>The compiler-selected result.</returns>
    internal static int CompilerRequireByteForDebugger() => RequireByteForDebugger(41);

    /// <summary>
    /// Marks the smaller applicable parameter for an integer constant.
    /// </summary>
    /// <param name="value">The converted byte constant.</param>
    /// <returns>The byte overload marker.</returns>
    internal static int SelectConstantForDebugger(byte value) => value + 9000;

    /// <summary>
    /// Marks the competing wider parameter for an integer constant.
    /// </summary>
    /// <param name="value">The converted short constant.</param>
    /// <returns>The short overload marker.</returns>
    internal static int SelectConstantForDebugger(short value) => value + 10000;

    /// <summary>
    /// Gets the compiler-selected overload for the integer constant.
    /// </summary>
    /// <returns>The compiler-selected overload marker.</returns>
    internal static int CompilerSelectConstantForDebugger() => SelectConstantForDebugger(41);

    /// <summary>
    /// Receives a long constant converted to an unsigned long parameter.
    /// </summary>
    /// <param name="value">The converted constant.</param>
    /// <returns>The unsigned result marker.</returns>
    internal static ulong RequireUlongForDebugger(ulong value) => value + 8000;

    /// <summary>
    /// Gets the compiler's result for a long constant passed to an unsigned long parameter.
    /// </summary>
    /// <returns>The compiler-selected result.</returns>
    internal static ulong CompilerRequireUlongForDebugger() => RequireUlongForDebugger(41L);

    /// <summary>
    /// Marks the native signed overload preferred before widening to Int64.
    /// </summary>
    /// <param name="value">The widened native integer argument.</param>
    /// <returns>The native integer overload marker.</returns>
    internal static long SelectNativeForDebugger(nint value) => checked((long)value) + 3000;

    /// <summary>
    /// Marks the Int64 overload competing with native integer widening.
    /// </summary>
    /// <param name="value">The widened Int64 argument.</param>
    /// <returns>The Int64 overload marker.</returns>
    internal static long SelectNativeForDebugger(long value) => value + 4000;

    /// <summary>
    /// Gets the compiler-selected native integer overload for a byte argument.
    /// </summary>
    /// <returns>The compiler-selected overload marker.</returns>
    internal static long CompilerSelectNativeForDebugger()
    {
        byte value = 41;
        return SelectNativeForDebugger(value);
    }

    /// <summary>
    /// Marks the native unsigned overload preferred before widening to UInt64.
    /// </summary>
    /// <param name="value">The widened native unsigned argument.</param>
    /// <returns>The native unsigned overload marker.</returns>
    internal static ulong SelectNativeUnsignedForDebugger(nuint value) => checked((ulong)value) + 5000;

    /// <summary>
    /// Marks the UInt64 overload competing with native unsigned widening.
    /// </summary>
    /// <param name="value">The widened UInt64 argument.</param>
    /// <returns>The UInt64 overload marker.</returns>
    internal static ulong SelectNativeUnsignedForDebugger(ulong value) => value + 6000;

    /// <summary>
    /// Gets the compiler-selected native unsigned overload for a UInt32 argument.
    /// </summary>
    /// <returns>The compiler-selected overload marker.</returns>
    internal static ulong CompilerSelectNativeUnsignedForDebugger()
    {
        uint value = 41;
        return SelectNativeUnsignedForDebugger(value);
    }

    /// <summary>
    /// Combines two values to expose the CLR parameter order of a named call.
    /// </summary>
    /// <param name="first">The high-order value.</param>
    /// <param name="second">The low-order value.</param>
    /// <returns>The order-sensitive result.</returns>
    internal static int CombineNamedForDebugger(int first, int second) => first * 100 + second;

    /// <summary>
    /// Exposes the source-language ordering rule for later positional arguments.
    /// </summary>
    /// <param name="first">The high-order value.</param>
    /// <param name="second">The middle value.</param>
    /// <param name="third">The low-order value.</param>
    /// <returns>The order-sensitive result.</returns>
    internal static int CombineThreeForDebugger(int first, int second, int third) =>
        first * 10000 + second * 100 + third;

    /// <summary>
    /// Exposes declaration defaults and named omissions to the debugger evaluator.
    /// </summary>
    /// <param name="first">The required high-order value.</param>
    /// <param name="second">The optional middle value.</param>
    /// <param name="third">The optional low-order value.</param>
    /// <returns>The order-sensitive result.</returns>
    internal static int CombineOptionalForDebugger(int first, int second = 42, int third = 43) =>
        first * 10000 + second * 100 + third;

    /// <summary>
    /// Identifies the exact-arity overload when an optional overload is also applicable.
    /// </summary>
    /// <param name="first">The supplied value.</param>
    /// <returns>The exact-arity marker.</returns>
    internal static int PreferExactForDebugger(int first) => first + 100;

    /// <summary>
    /// Identifies the optional overload when its second parameter is supplied.
    /// </summary>
    /// <param name="first">The first supplied value.</param>
    /// <param name="second">The optional second value.</param>
    /// <returns>The two-parameter marker.</returns>
    internal static int PreferExactForDebugger(int first, int second = 99) => first * 100 + second;

    /// <summary>
    /// Exposes a metadata string default to target-side execution.
    /// </summary>
    /// <param name="text">The optional string value.</param>
    /// <returns>The selected text.</returns>
    internal static string OptionalStringForDebugger(string text = "loaded default") => text;

    /// <summary>
    /// Exposes a null reference default to target-side execution.
    /// </summary>
    /// <param name="text">The optional reference value.</param>
    /// <returns>One when the default null was supplied.</returns>
    internal static int OptionalNullForDebugger(string? text = null) => text is null ? 1 : 0;

    /// <summary>
    /// Exposes an enum default stored as its underlying metadata constant.
    /// </summary>
    /// <param name="mode">The optional signed enum value.</param>
    /// <returns>The target-observed underlying value.</returns>
    internal static int OptionalEnumForDebugger(DebuggerFixtureMode mode = DebuggerFixtureMode.First) =>
        (short)mode;

    /// <summary>
    /// Exposes a flags enum default stored as a signed 32-bit metadata constant.
    /// </summary>
    /// <param name="options">The optional flags value.</param>
    /// <returns>The target-observed underlying value.</returns>
    internal static int OptionalFlagsForDebugger(DebuggerFixtureOptions options = DebuggerFixtureOptions.ReadWrite) =>
        (int)options;

    /// <summary>
    /// Exposes a high-bit unsigned enum default from loaded metadata.
    /// </summary>
    /// <param name="mode">The optional unsigned wide enum value.</param>
    /// <returns>One when the target receives the high-bit value.</returns>
    internal static int OptionalWideEnumForDebugger(DebuggerFixtureWideMode mode = DebuggerFixtureWideMode.High) =>
        mode == DebuggerFixtureWideMode.High ? 1 : 0;

    /// <summary>
    /// Exposes an optional enum declared by another loaded assembly.
    /// </summary>
    /// <param name="share">The optional framework enum value.</param>
    /// <returns>The target-observed underlying value.</returns>
    internal static int OptionalExternalEnumForDebugger(FileShare share = FileShare.ReadWrite) =>
        (int)share;

    /// <summary>
    /// Exposes a negative scaled decimal default through compiler metadata.
    /// </summary>
    /// <param name="amount">The optional decimal value.</param>
    /// <returns>One when the target receives the declared value.</returns>
    internal static int OptionalDecimalForDebugger(decimal amount = -12.50m)
    {
        int[] bits = decimal.GetBits(amount);
        return bits is [1250, 0, 0, unchecked((int)0x80020000)] ? 1 : 0;
    }

    /// <summary>
    /// Observes a runtime integer widened to an exact decimal argument.
    /// </summary>
    /// <param name="amount">The converted decimal value.</param>
    /// <returns>One when the target receives the integer with scale zero.</returns>
    internal static int DecimalFromIntegerForDebugger(decimal amount) =>
        decimal.GetBits(amount) is [41, 0, 0, 0] ? 1 : 0;

    /// <summary>
    /// Exposes a nonzero DateTime default through compiler-recognized metadata.
    /// </summary>
    /// <param name="date">The optional date value.</param>
    /// <returns>One when the target receives the declared ticks.</returns>
    internal static int OptionalDateTimeForDebugger(
        [System.Runtime.InteropServices.Optional]
        [DateTimeConstant(637134336000000000L)] DateTime date) =>
        date.Ticks == 637134336000000000L && date.Kind == DateTimeKind.Unspecified ? 1 : 0;

    /// <summary>
    /// Observes the compiler's default for an optional integer without a constant.
    /// </summary>
    /// <param name="number">The optional integer value.</param>
    /// <returns>One when the target receives zero.</returns>
    internal static int OptionalIntegerWithoutConstantForDebugger(
        [System.Runtime.InteropServices.Optional] int number) => number == 0 ? 1 : 0;

    /// <summary>
    /// Observes the compiler's default for an optional Boolean without a constant.
    /// </summary>
    /// <param name="enabled">The optional Boolean value.</param>
    /// <returns>One when the target receives false.</returns>
    internal static int OptionalBooleanWithoutConstantForDebugger(
        [System.Runtime.InteropServices.Optional] bool enabled) => enabled ? 0 : 1;

    /// <summary>
    /// Observes the compiler's default for an optional external enum without a constant.
    /// </summary>
    /// <param name="share">The optional enum value.</param>
    /// <returns>One when the target receives its zero member.</returns>
    internal static int OptionalEnumWithoutConstantForDebugger(
        [System.Runtime.InteropServices.Optional] FileShare share) => share == FileShare.None ? 1 : 0;

    /// <summary>
    /// Observes the compiler's default for an optional string without a constant.
    /// </summary>
    /// <param name="text">The optional string value.</param>
    /// <returns>One when the target receives null.</returns>
    internal static int OptionalStringWithoutConstantForDebugger(
        [System.Runtime.InteropServices.Optional] string? text) => text is null ? 1 : 0;

    /// <summary>
    /// Observes the compiler's default for an optional decimal without a constant.
    /// </summary>
    /// <param name="amount">The optional decimal value.</param>
    /// <returns>One when the target receives zero with scale zero.</returns>
    internal static int OptionalDecimalWithoutConstantForDebugger(
        [System.Runtime.InteropServices.Optional] decimal amount) =>
        decimal.GetBits(amount) is [0, 0, 0, 0] ? 1 : 0;

    /// <summary>
    /// Observes the compiler's default for an optional DateTime without a constant.
    /// </summary>
    /// <param name="date">The optional DateTime value.</param>
    /// <returns>One when the target receives its zero representation.</returns>
    internal static int OptionalDateTimeWithoutConstantForDebugger(
        [System.Runtime.InteropServices.Optional] DateTime date) =>
        date.Ticks == 0 && date.Kind == DateTimeKind.Unspecified ? 1 : 0;

    /// <summary>
    /// Observes the compiler's default for an optional ordinary value type.
    /// </summary>
    /// <param name="value">The optional globally unique identifier.</param>
    /// <returns>One when the target receives a zero-initialized value.</returns>
    internal static int OptionalGuidWithoutConstantForDebugger(
        [System.Runtime.InteropServices.Optional] Guid value) => value == Guid.Empty ? 1 : 0;

    /// <summary>
    /// Gets the compiler's result for the omitted globally unique identifier.
    /// </summary>
    /// <returns>The target-observed result.</returns>
    internal static int CompilerOptionalGuidWithoutConstantForDebugger() =>
        OptionalGuidWithoutConstantForDebugger();

    /// <summary>
    /// Observes the compiler's default for a generic value type containing a reference.
    /// </summary>
    /// <param name="value">The optional key-value pair.</param>
    /// <returns>One when both fields have their default values.</returns>
    internal static int OptionalPairWithoutConstantForDebugger(
        [System.Runtime.InteropServices.Optional] KeyValuePair<int, string?> value) =>
        value.Key == 0 && value.Value is null ? 1 : 0;

    /// <summary>
    /// Gets the compiler's result for the omitted generic value type.
    /// </summary>
    /// <returns>The target-observed result.</returns>
    internal static int CompilerOptionalPairWithoutConstantForDebugger() =>
        OptionalPairWithoutConstantForDebugger();

    /// <summary>
    /// Observes the compiler's default for an optional nullable value type.
    /// </summary>
    /// <param name="value">The optional nullable integer.</param>
    /// <returns>One when the target receives an empty nullable value.</returns>
    internal static int OptionalNullableWithoutConstantForDebugger(
        [System.Runtime.InteropServices.Optional] int? value) => value.HasValue ? 0 : 1;

    /// <summary>
    /// Gets the compiler's result for the omitted nullable value type.
    /// </summary>
    /// <returns>The target-observed result.</returns>
    internal static int CompilerOptionalNullableWithoutConstantForDebugger() =>
        OptionalNullableWithoutConstantForDebugger();

    /// <summary>
    /// Reads an authored struct carried through nullable storage.
    /// </summary>
    /// <param name="value">The nullable authored value type.</param>
    /// <returns>The stored number, or minus one for an empty nullable value.</returns>
    internal static int ReadNullableStructForDebugger(DebuggerOptionalStructFixture? value) =>
        value.HasValue ? value.Value._number : -1;

    /// <summary>
    /// Observes an optional struct without invoking its explicit parameterless constructor.
    /// </summary>
    /// <param name="value">The optional authored value type.</param>
    /// <returns>One when its fields retain their zero-initialized values.</returns>
    internal static int OptionalStructWithoutConstantForDebugger(
        [System.Runtime.InteropServices.Optional] DebuggerOptionalStructFixture value) =>
        value._number == 0 && value._text is null ? 1 : 0;

    /// <summary>
    /// Gets the compiler's result for the omitted authored value type.
    /// </summary>
    /// <returns>The target-observed result.</returns>
    internal static int CompilerOptionalStructWithoutConstantForDebugger() =>
        OptionalStructWithoutConstantForDebugger();

    /// <summary>
    /// Returns an inferred generic argument through a target-compiled method.
    /// </summary>
    /// <typeparam name="T">The inferred argument and result type.</typeparam>
    /// <param name="value">The value preserved across the generic call.</param>
    /// <returns>The original value.</returns>
    internal static T IdentityGenericForDebugger<T>(T value) => value;

    /// <summary>
    /// Reads an element after inferring the method type from an array shape.
    /// </summary>
    /// <typeparam name="T">The inferred array element type.</typeparam>
    /// <param name="values">The target's array storage.</param>
    /// <returns>The first element.</returns>
    internal static T FirstGenericArrayForDebugger<T>(T[] values) => values[0];

    /// <summary>
    /// Reads an element after inferring the method type from a constructed collection.
    /// </summary>
    /// <typeparam name="T">The inferred collection element type.</typeparam>
    /// <param name="values">The target's collection.</param>
    /// <returns>The first element.</returns>
    internal static T FirstGenericListForDebugger<T>(List<T> values) => values[0];

    /// <summary>
    /// Reads an element after inferring through a loaded enumerable interface.
    /// </summary>
    /// <typeparam name="T">The inferred sequence element type.</typeparam>
    /// <param name="values">The target sequence.</param>
    /// <returns>The first element.</returns>
    internal static T FirstGenericEnumerableForDebugger<T>(IEnumerable<T> values) => values.First();

    /// <summary>
    /// Gets the C# compiler's interface-based generic inference result.
    /// </summary>
    /// <returns>The first compiler-inferred array element.</returns>
    internal static int CompilerFirstGenericEnumerableForDebugger()
    {
        int[] values = [41];
        return FirstGenericEnumerableForDebugger(values);
    }

    /// <summary>
    /// Gets the C# compiler's base-class generic inference result.
    /// </summary>
    /// <returns>The first compiler-inferred list element.</returns>
    internal static int CompilerFirstGenericBaseForDebugger() =>
        FirstGenericListForDebugger(new DebuggerFixtureList(81));

    /// <summary>
    /// Reads a struct passed through an inferred generic value parameter.
    /// </summary>
    /// <typeparam name="T">The inferred value type.</typeparam>
    /// <param name="value">The unboxed target value.</param>
    /// <returns>The struct's number, or minus one for a different type.</returns>
    internal static int StructNumberGenericForDebugger<T>(T value) =>
        value is DebuggerOptionalStructFixture fixture ? fixture.ReadNumber() : -1;

    /// <summary>
    /// Supplies a generic overload competing with an exact non-generic method.
    /// </summary>
    /// <typeparam name="T">The inferred argument type.</typeparam>
    /// <param name="value">The supplied value.</param>
    /// <returns>The generic overload marker.</returns>
    internal static int PreferNonGenericForDebugger<T>(T value) => 1;

    /// <summary>
    /// Supplies the exact non-generic overload selected by the C# compiler.
    /// </summary>
    /// <param name="value">The supplied integer.</param>
    /// <returns>The non-generic overload marker.</returns>
    internal static int PreferNonGenericForDebugger(int value) => 2;

    /// <summary>
    /// Identifies the common type inferred from two independently typed arguments.
    /// </summary>
    /// <typeparam name="T">The common inferred argument type.</typeparam>
    /// <param name="first">The first supplied value.</param>
    /// <param name="second">The second supplied value.</param>
    /// <returns>A marker identifying the inferred runtime type.</returns>
    internal static int CommonGenericForDebugger<T>(T first, T second)
    {
        GC.KeepAlive(first);
        GC.KeepAlive(second);
        if (typeof(T) == typeof(int))
        {
            return 32;
        }

        if (typeof(T) == typeof(long))
        {
            return 64;
        }

        return typeof(T) == typeof(List<int>) ? 71 : 0;
    }

    /// <summary>
    /// Identifies the common type inferred after considering three independent bounds.
    /// </summary>
    /// <typeparam name="T">The common inferred argument type.</typeparam>
    /// <param name="first">The first supplied value.</param>
    /// <param name="second">The second supplied value.</param>
    /// <param name="third">The third supplied value.</param>
    /// <returns>One when the common loaded type is System.Object.</returns>
    internal static int CommonGenericForDebugger<T>(T first, T second, T third)
    {
        GC.KeepAlive(first);
        GC.KeepAlive(second);
        GC.KeepAlive(third);
        return typeof(T) == typeof(object) ? 1 : 0;
    }

    /// <summary>
    /// Gets the C# compiler's common numeric inference result.
    /// </summary>
    /// <returns>The marker produced for the compiler-inferred Int64 type.</returns>
    internal static int CompilerCommonNumericForDebugger() => CommonGenericForDebugger(41, 42L);

    /// <summary>
    /// Gets the C# compiler's common reference inference result.
    /// </summary>
    /// <param name="first">A loaded list subtype.</param>
    /// <param name="second">An unrelated loaded reference type.</param>
    /// <param name="third">An object-typed reference.</param>
    /// <returns>The marker produced for the compiler-inferred System.Object type.</returns>
    internal static int CompilerCommonReferenceForDebugger(
        DebuggerFixtureList first,
        StrongBox<int> second,
        object third) => CommonGenericForDebugger(first, second, third);

    /// <summary>
    /// Receives an inferred type satisfying a reference-type constraint.
    /// </summary>
    /// <typeparam name="T">The inferred reference type.</typeparam>
    /// <param name="value">The supplied reference.</param>
    /// <returns>The reference-type constraint marker.</returns>
    internal static int ClassConstraintForDebugger<T>(T value) where T : class => value is null ? 0 : 11;

    /// <summary>
    /// Receives an inferred type satisfying a nonnullable value-type constraint.
    /// </summary>
    /// <typeparam name="T">The inferred value type.</typeparam>
    /// <param name="value">The supplied value.</param>
    /// <returns>The value-type constraint marker.</returns>
    internal static int StructConstraintForDebugger<T>(T value) where T : struct =>
        value.GetHashCode() == 41 ? 22 : 0;

    /// <summary>
    /// Receives an inferred type implementing a loaded generic interface.
    /// </summary>
    /// <typeparam name="T">The inferred collection type.</typeparam>
    /// <param name="value">The supplied collection.</param>
    /// <returns>The collection's element count.</returns>
    internal static int InterfaceConstraintForDebugger<T>(T value) where T : IReadOnlyList<int> => value.Count;

    /// <summary>
    /// Receives an inferred derived type satisfying a loaded base-class constraint.
    /// </summary>
    /// <typeparam name="T">The inferred list subtype.</typeparam>
    /// <param name="value">The supplied derived list.</param>
    /// <returns>The first list value.</returns>
    internal static int BaseConstraintForDebugger<T>(T value) where T : List<int> => value[0];

    /// <summary>
    /// Receives an inferred type with a public parameterless constructor.
    /// </summary>
    /// <typeparam name="T">The inferred constructible type.</typeparam>
    /// <param name="value">The supplied object.</param>
    /// <returns>The constructor-constraint marker.</returns>
    internal static int ConstructorConstraintForDebugger<T>(T value) where T : new()
    {
        GC.KeepAlive(value);
        return 44;
    }

    /// <summary>
    /// Supplies an unmanaged constraint for a value with reference-containing storage.
    /// </summary>
    /// <typeparam name="T">The compiler-checked unmanaged type.</typeparam>
    /// <param name="value">The supplied value.</param>
    /// <returns>The unmanaged constraint marker.</returns>
    internal static int UnmanagedConstraintForDebugger<T>(T value) where T : unmanaged
    {
        GC.KeepAlive(value);
        return 55;
    }

    /// <summary>
    /// Observes a contextual default passed to an object parameter.
    /// </summary>
    /// <param name="value">The target-typed object value.</param>
    /// <returns>One when the target receives null.</returns>
    internal static int ContextualObjectForDebugger(object? value) => value is null ? 1 : 0;

    /// <summary>
    /// Identifies an object overload selected from a contextual default.
    /// </summary>
    /// <param name="value">The target-typed object value.</param>
    /// <returns>Two when the object overload executes.</returns>
    internal static int PreferContextualReferenceForDebugger(object? value) => value is null ? 2 : 0;

    /// <summary>
    /// Identifies the more specific string overload for a contextual default.
    /// </summary>
    /// <param name="value">The target-typed string value.</param>
    /// <returns>One when the string overload executes.</returns>
    internal static int PreferContextualReferenceForDebugger(string? value) => value is null ? 1 : 0;

    /// <summary>
    /// Runs the compiler-selected overload for a contextual default.
    /// </summary>
    /// <returns>The target-observed overload marker.</returns>
    internal static int CompilerContextualReferenceForDebugger() =>
        PreferContextualReferenceForDebugger(default);

    /// <summary>
    /// Runs the compiler-selected object overload for a typed default.
    /// </summary>
    /// <returns>The target-observed overload marker.</returns>
    internal static int CompilerTypedReferenceForDebugger() =>
        PreferContextualReferenceForDebugger(default(object));

    /// <summary>
    /// Identifies the preferred integer overload for a contextual default.
    /// </summary>
    /// <param name="value">The target-typed integer value.</param>
    /// <returns>One when the integer overload executes.</returns>
    internal static int PreferContextualNumericForDebugger(int value) => value == 0 ? 1 : 0;

    /// <summary>
    /// Identifies the wider numeric overload for a contextual default.
    /// </summary>
    /// <param name="value">The target-typed long value.</param>
    /// <returns>Two when the long overload executes.</returns>
    internal static int PreferContextualNumericForDebugger(long value) => value == 0 ? 2 : 0;

    /// <summary>
    /// Runs the compiler-selected numeric overload for a contextual default.
    /// </summary>
    /// <returns>The target-observed overload marker.</returns>
    internal static int CompilerContextualNumericForDebugger() =>
        PreferContextualNumericForDebugger(default);

    /// <summary>
    /// Runs the compiler-selected long overload for a typed default.
    /// </summary>
    /// <returns>The target-observed overload marker.</returns>
    internal static int CompilerTypedNumericForDebugger() =>
        PreferContextualNumericForDebugger(default(long));

    /// <summary>
    /// Supplies a value-type candidate for an ambiguous contextual default.
    /// </summary>
    /// <param name="value">The target-typed integer value.</param>
    /// <returns>The supplied value.</returns>
    internal static int AmbiguousContextualDefaultForDebugger(int value) => value;

    /// <summary>
    /// Supplies a reference-type candidate for an ambiguous contextual default.
    /// </summary>
    /// <param name="value">The target-typed string value.</param>
    /// <returns>The supplied string length.</returns>
    internal static int AmbiguousContextualDefaultForDebugger(string? value) => value?.Length ?? 0;

    /// <summary>
    /// Announces initialized arrays after entering an observed runtime wait owned by the capturing test.
    /// </summary>
    /// <param name="path">The test-owned capture identity retained in the frame.</param>
    /// <param name="announcement">The readiness bytes expected by the capture process.</param>
    /// <returns>Zero after the arrays survive the inspection interval.</returns>
    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.NoOptimization)]
    internal static int Run(string path, string announcement)
    {
        int[] vector = [41, 42, 43];
        int[] empty = [];
        int[] singleton = [91];
        int[]? absent = null;
        var rectangular = Array.CreateInstance(typeof(int), [2, 3]);
        var nonZero = Array.CreateInstance(typeof(int), [2, 3], [-2, 5]);
        for (int row = -2; row < 0; row++)
        {
            for (int column = 5; column < 8; column++)
            {
                nonZero.SetValue((row + 3) * 10 + column, row, column);
                rectangular.SetValue((row + 3) * 10 + column - 4, row + 2, column - 5);
            }
        }
        int[][] jagged = [vector, singleton, empty, vector];
        int[][] many = new int[65537][];
        Array.Fill(many, vector);
        var nonVector = Array.CreateInstance(typeof(int), [2], [-3]);
        nonVector.SetValue(71, -3);
        nonVector.SetValue(72, -2);
        var emptyDimension = Array.CreateInstance(typeof(int), [3, 0]);
        string?[] texts = ["captured\ntext", null, "last"];
        int[] large = new int[65537];
        for (int index = 0; index < large.Length; index++)
        {
            large[index] = index + 100;
        }
        ResultsViewFixture<int> results = new(large);
        int resultsViewWarmup = Enumerable.Range(0, 1).Sum();
        object[] cycle = new object[1];
        cycle[0] = cycle;
        List<int>?[] constructed = [[81, 82], null];
        (int, string)[] tuples = [(73, "captured pair")];
        (int, int)[] unmanagedTuples = [(17, 23)];
        int?[] nullable = [37, null];
        DebuggerOptionalStructFixture?[] nullableStructs = [new DebuggerOptionalStructFixture(), null];
        decimal[] decimals = [12.5m];
        DebuggerOptionalStructFixture[] optionalStructs = [new()];
        Dictionary<int, List<string>> dictionary = new() { [71] = ["captured"] };
        Dictionary<int, List<string>>.KeyCollection?[] nested = [dictionary.Keys, null];
        Dictionary<int, List<string>>.KeyCollection.Enumerator[] nestedEnumerators = [default];
        object emptyObject = new();
        StrongBox<int> singletonObject = new(29);
        StrongBox<int>? absentObject = null;
        DebuggerFixtureList inheritedObject = new(81);
        DebuggerAmbiguousEnumerable ambiguousEnumerable = new();
        IDebuggerInterfaceFixture<int> implicitInterface = new DebuggerImplicitInterfaceFixture();
        IDebuggerInterfaceFixture<int> explicitInterface = new DebuggerExplicitInterfaceFixture();
        IDebuggerDerivedInterfaceFixture derivedInterface = new DebuggerDerivedInterfaceFixture();
        object explicitInterfaceObject = explicitInterface;
        DebuggerFixtureValue capturedObject = new(42, "answer!", path);
        object boxedPair = (73, "captured pair");
        object boxedNullableValue = 47;
        object? boxedNullableEmpty = null;
        object boxedNullableMismatch = "not an integer";
        object boxedNullableStruct = new DebuggerOptionalStructFixture();
        ValueType boxedIntegerValue = 47;
        ValueType boxedDecimalValue = 47m;
        ValueType boxedStructValue = new DebuggerOptionalStructFixture();
        DebuggerImplicitConversionSource implicitConversion = new(41);
        DebuggerImplicitConversionSource emptyImplicitConversion = new(0);
        DebuggerImplicitConversionSource throwingImplicitConversion = new(-1);
        DebuggerImplicitConversionSource? populatedLiftedImplicitConversion = implicitConversion;
        DebuggerImplicitConversionSource? emptyLiftedImplicitConversion = null;
        DebuggerExplicitNullableResultSource? populatedNullableResultConversion = new(41);
        DebuggerExplicitNullableResultSource? nullNullableResultConversion = new(0);
        DebuggerExplicitNullableResultSource? emptyNullableResultConversion = null;
        DebuggerExplicitNumericResultSource explicitNumericResultSource = new(41);
        DebuggerImplicitConversionReferenceDerived implicitReferenceConversion = new(41);
        DebuggerImplicitConversionReferenceSource explicitReferenceInput =
            implicitReferenceConversion;
        DebuggerImplicitConversionReferenceSource invalidExplicitReferenceInput = new(41);
        DebuggerExplicitReferenceResultSource explicitReferenceResultSource = new(41);
        DebuggerExplicitReferenceResultSource invalidExplicitReferenceResultSource = new(-1);
        double explicitConversionResult = 0;
        string? explicitLiftedConversionResult = "initial";
        double? explicitLiftedValueResult = null;
        StrongBox<object[]> chain = new([new StrongBox<int[]>(vector)]);
        StrongBox<object?> cycleObject = new();
        cycleObject.Value = cycleObject;
        DebuggerDumpHiddenField hiddenFields = new();
        DebuggerDumpNestedFields inlineFields = new();
        DebuggerBlockingWait.Wait(announcement);
        GC.KeepAlive(path);
        GC.KeepAlive(vector);
        GC.KeepAlive(empty);
        GC.KeepAlive(singleton);
        GC.KeepAlive(absent);
        GC.KeepAlive(rectangular);
        GC.KeepAlive(nonZero);
        GC.KeepAlive(jagged);
        GC.KeepAlive(many);
        GC.KeepAlive(nonVector);
        GC.KeepAlive(emptyDimension);
        GC.KeepAlive(texts);
        GC.KeepAlive(large);
        GC.KeepAlive(results);
        GC.KeepAlive(resultsViewWarmup);
        GC.KeepAlive(cycle);
        GC.KeepAlive(constructed);
        GC.KeepAlive(tuples);
        GC.KeepAlive(unmanagedTuples);
        GC.KeepAlive(nullable);
        GC.KeepAlive(nullableStructs);
        GC.KeepAlive(decimals);
        GC.KeepAlive(optionalStructs);
        GC.KeepAlive(nested);
        GC.KeepAlive(nestedEnumerators);
        GC.KeepAlive(emptyObject);
        GC.KeepAlive(singletonObject);
        GC.KeepAlive(absentObject);
        GC.KeepAlive(inheritedObject);
        GC.KeepAlive(ambiguousEnumerable);
        GC.KeepAlive(implicitInterface);
        GC.KeepAlive(explicitInterface);
        GC.KeepAlive(derivedInterface);
        GC.KeepAlive(explicitInterfaceObject);
        GC.KeepAlive(capturedObject);
        GC.KeepAlive(boxedPair);
        GC.KeepAlive(boxedNullableValue);
        GC.KeepAlive(boxedNullableEmpty);
        GC.KeepAlive(boxedNullableMismatch);
        GC.KeepAlive(boxedNullableStruct);
        GC.KeepAlive(boxedIntegerValue);
        GC.KeepAlive(boxedDecimalValue);
        GC.KeepAlive(boxedStructValue);
        GC.KeepAlive(implicitConversion);
        GC.KeepAlive(emptyImplicitConversion);
        GC.KeepAlive(throwingImplicitConversion);
        GC.KeepAlive(populatedLiftedImplicitConversion);
        GC.KeepAlive(emptyLiftedImplicitConversion);
        GC.KeepAlive(populatedNullableResultConversion);
        GC.KeepAlive(nullNullableResultConversion);
        GC.KeepAlive(emptyNullableResultConversion);
        GC.KeepAlive(explicitNumericResultSource);
        GC.KeepAlive(explicitReferenceInput);
        GC.KeepAlive(invalidExplicitReferenceInput);
        GC.KeepAlive(explicitReferenceResultSource);
        GC.KeepAlive(invalidExplicitReferenceResultSource);
        GC.KeepAlive(explicitConversionResult);
        GC.KeepAlive(explicitLiftedConversionResult);
        GC.KeepAlive(explicitLiftedValueResult);
        GC.KeepAlive(implicitReferenceConversion);
        GC.KeepAlive(chain);
        GC.KeepAlive(cycleObject);
        GC.KeepAlive(hiddenFields.GetDerivedValue());
        GC.KeepAlive(inlineFields);
        return 0;
    }
}
