namespace Csls.TestProcessHost;

/// <summary>
/// Provides stable iterator source positions for managed stepping tests.
/// </summary>
internal static class DebuggerIteratorStepFixture
{
    /// <summary>
    /// Enumerates a compiler-generated iterator from user-authored calling code.
    /// </summary>
    /// <returns>Zero when enumeration preserves the expected values.</returns>
    internal static int Run()
    {
        int total = 0;
        foreach (int value in EnumerateValues())
        {
            total += value;
        }

        return total == 3 ? 0 : 1;
    }

    private static IEnumerable<int> EnumerateValues()
    {
        int value = 1;
        yield return value;
        value++;
        yield return checked(value);
    }
}
