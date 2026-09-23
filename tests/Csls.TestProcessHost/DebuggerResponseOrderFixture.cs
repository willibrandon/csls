namespace Csls.TestProcessHost;

/// <summary>
/// Runs adjacent statements whose breakpoint and step callbacks can immediately follow resumption.
/// </summary>
internal static class DebuggerResponseOrderFixture
{
    /// <summary>
    /// Accumulates a bounded loop and verifies that debugging preserved every iteration.
    /// </summary>
    internal static int Run()
    {
        int total = 0;
        for (int index = 0; index < 32; index++)
        {
            total += index;
            int expected = index * (index + 1) / 2;
            if (total != expected)
            {
                return 1;
            }
        }

        return total == 496 ? 0 : 1;
    }
}
