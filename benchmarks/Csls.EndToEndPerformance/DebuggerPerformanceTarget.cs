using System.Runtime.CompilerServices;

namespace Csls.EndToEndPerformance;

/// <summary>
/// Supplies live scalar and indexed values at a reproducible source breakpoint.
/// </summary>
internal static class DebuggerPerformanceTarget
{
    /// <summary>
    /// Gets the compiler-recorded path of the target's original source document.
    /// </summary>
    internal static string SourcePath => GetSourcePath();

    /// <summary>
    /// Writes independently computed values after the debugger resumes the stopped target.
    /// </summary>
    /// <param name="seed">The input retained in the inspected frame.</param>
    /// <returns>Zero when the retained values have their expected contents.</returns>
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static int Run(int seed)
    {
        int answer = seed + 2;
        int[] numbers = [.. Enumerable.Range(1, 256)];
        Console.WriteLine($"debugger-performance-output:{answer}:{numbers.Length}"); // debugger performance stop
        GC.KeepAlive(numbers);
        return answer == 42 && numbers[255] == 256 ? 0 : 1;
    }

    private static string GetSourcePath([CallerFilePath] string sourcePath = "") => sourcePath;
}
