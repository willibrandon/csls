using System.Runtime.CompilerServices;

namespace Csls.TestProcessHost;

/// <summary>
/// Retains distinct CLR array layouts while an independent process captures its heap.
/// </summary>
internal static class DebuggerDumpArrayFixture
{
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
        object[] cycle = new object[1];
        cycle[0] = cycle;
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
        GC.KeepAlive(cycle);
        return 0;
    }
}
