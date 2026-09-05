using System.Runtime.CompilerServices;

namespace Csls.TestProcessHost;

/// <summary>
/// Provides stable source positions for managed stepping integration tests.
/// </summary>
internal static class DebuggerStepFixture
{
    /// <summary>
    /// Executes a caller and callee with distinct Portable PDB sequence points.
    /// </summary>
    /// <param name="path">The signal file path that permits the fixture to exit.</param>
    /// <returns>Zero when stepping did not disturb the computed result.</returns>
    internal static int Run(string path)
    {
        int seed = 40;
        int answer = AddTwo(seed);
        int combined = AddTwo(seed - 1) + AddTwo(seed);
        while (!File.Exists(path))
        {
            Thread.Sleep(1);
        }

        return answer == 42 && combined == 83 ? 0 : 1;
    }

    /// <summary>
    /// Adds two through a non-inlined managed callee.
    /// </summary>
    /// <param name="value">The value to increment.</param>
    /// <returns>The value increased by two.</returns>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int AddTwo(int value)
    {
        int result = value + 2;
        return result;
    }
}
