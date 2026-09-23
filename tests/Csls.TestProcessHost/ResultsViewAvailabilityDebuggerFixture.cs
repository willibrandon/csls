namespace Csls.TestProcessHost;

/// <summary>
/// Stops with a real enumerable before any LINQ assembly is requested by the target.
/// </summary>
internal static class ResultsViewAvailabilityDebuggerFixture
{
    /// <summary>
    /// Keeps the collection live without loading the optional runtime debug-view implementation.
    /// </summary>
    /// <param name="path">The signal file that releases the target.</param>
    /// <param name="announcement">The text emitted when fixture setup completes.</param>
    /// <returns>Zero after the test releases the stopped target.</returns>
    internal static int WaitForSignal(string path, string announcement)
    {
        var localResultsViewUnavailable = new ResultsViewFixture<int>([161, 162]);
        Console.Write(announcement);
        Console.Out.Flush();
        while (!File.Exists(path))
        {
            Thread.Sleep(1);
        }

        GC.KeepAlive(localResultsViewUnavailable);
        return 0;
    }
}
