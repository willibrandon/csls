using System.Globalization;

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
        int answer = int.Parse(arguments[1], CultureInfo.InvariantCulture);
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
            Thread.SpinWait(10_000);
        }

        return answer - 42;
    }
}
