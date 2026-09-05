using System.Globalization;
using System.Runtime.CompilerServices;

namespace Csls.Debugger.Fixtures.CSharp;

/// <summary>
/// Provides the C# debugger integration fixture entry point.
/// </summary>
internal static class Program
{
    /// <summary>
    /// Waits after executing a stable source statement for debugger inspection.
    /// </summary>
    /// <param name="arguments">The target signal, value, output, and optional progress paths.</param>
    /// <returns>Zero when the expected local value remains live.</returns>
    internal static int Main(string[] arguments)
    {
        if (arguments is ["--tiered-compilation", _, _, _, _])
        {
            return RunTieredCompilation(arguments[1..]);
        }

        int answer = int.Parse(arguments[1], CultureInfo.InvariantCulture);
        var value = new DebuggerFixtureValue(answer);
        var genericValue = new DebuggerGenericFixture<int>(answer);
        var nestedGenericValue = new DebuggerGenericFixture<List<int>>([]);
        var arrayGenericValue = new DebuggerGenericFixture<int[]>([]);
        var nullableGenericValue = new DebuggerGenericFixture<int?>(answer);
        ValueTuple<int, int>[] pairs = [new(0, 142), new(151, 152)];
        if (arguments.Length >= 5)
        {
            File.WriteAllText(arguments[3], "started");
        }

        answer++;
        if (arguments.Length >= 5)
        {
            File.WriteAllText(arguments[4], "continued");
        }

        Console.Write(arguments[2]);
        Console.Out.Flush();
        while (!File.Exists(arguments[0]))
        {
            Thread.Sleep(1);
        }

        GC.KeepAlive(value);
        GC.KeepAlive(genericValue);
        GC.KeepAlive(nestedGenericValue);
        GC.KeepAlive(arrayGenericValue);
        GC.KeepAlive(nullableGenericValue);
        GC.KeepAlive(pairs);
        return answer - 42;
    }

    private static int RunTieredCompilation(string[] arguments)
    {
        Console.Out.Write("ready");
        Console.Out.Flush();
        WaitForFile(arguments[0]);
        int checksum = 0;
        for (int iteration = 0; iteration < 250_000; iteration++)
        {
            checksum ^= IncrementTieredValue(iteration);
        }

        Thread.Sleep(500);
        File.WriteAllText(arguments[1], checksum.ToString(CultureInfo.InvariantCulture));
        WaitForFile(arguments[2]);
        int answer = IncrementTieredValue(41);
        WaitForFile(arguments[3]);
        return answer == 42 ? 0 : 1;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int IncrementTieredValue(int value)
    {
        int tieredAnswer = value + 1;
        return tieredAnswer;
    }

    private static void WaitForFile(string path)
    {
        while (!File.Exists(path))
        {
            Thread.Sleep(1);
        }
    }
}
