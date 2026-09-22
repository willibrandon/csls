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
        int?[] nullable = [37, null];
        decimal[] decimals = [12.5m];
        Dictionary<int, List<string>> dictionary = new() { [71] = ["captured"] };
        Dictionary<int, List<string>>.KeyCollection?[] nested = [dictionary.Keys, null];
        Dictionary<int, List<string>>.KeyCollection.Enumerator[] nestedEnumerators = [default];
        object emptyObject = new();
        StrongBox<int> singletonObject = new(29);
        StrongBox<int>? absentObject = null;
        DebuggerFixtureList inheritedObject = new(81);
        DebuggerFixtureValue capturedObject = new(42, "answer!", path);
        object boxedPair = (73, "captured pair");
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
        GC.KeepAlive(nullable);
        GC.KeepAlive(decimals);
        GC.KeepAlive(nested);
        GC.KeepAlive(nestedEnumerators);
        GC.KeepAlive(emptyObject);
        GC.KeepAlive(singletonObject);
        GC.KeepAlive(absentObject);
        GC.KeepAlive(inheritedObject);
        GC.KeepAlive(capturedObject);
        GC.KeepAlive(boxedPair);
        GC.KeepAlive(chain);
        GC.KeepAlive(cycleObject);
        GC.KeepAlive(hiddenFields.GetDerivedValue());
        GC.KeepAlive(inlineFields);
        return 0;
    }
}
