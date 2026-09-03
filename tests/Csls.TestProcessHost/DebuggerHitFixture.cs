using System.Globalization;

namespace Csls.TestProcessHost;

/// <summary>
/// Produces repeatable managed source and function breakpoint hits.
/// </summary>
internal static class DebuggerHitFixture
{
    /// <summary>
    /// Calls one stable managed method repeatedly before waiting for termination.
    /// </summary>
    /// <param name="signalPath">The file that allows normal process completion.</param>
    /// <param name="progressPath">The file recording the current one-based hit.</param>
    /// <param name="hitCount">The number of managed calls to make.</param>
    /// <returns>Zero after the signal file appears.</returns>
    internal static int Run(string signalPath, string progressPath, int hitCount)
    {
        for (int hit = 1; hit <= hitCount; hit++)
        {
            File.WriteAllText(
                progressPath,
                hit.ToString(CultureInfo.InvariantCulture));
            RecordHit(hit);
        }

        while (!File.Exists(signalPath))
        {
            Thread.SpinWait(10_000);
        }

        return 0;
    }

    /// <summary>
    /// Provides one stable function entry and source sequence point per requested hit.
    /// </summary>
    /// <param name="hit">The current one-based hit.</param>
    internal static void RecordHit(int hit)
    {
        int observedHit = hit;
        GC.KeepAlive(observedHit);
    }
}
