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
