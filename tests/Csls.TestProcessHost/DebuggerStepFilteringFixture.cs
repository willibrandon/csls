namespace Csls.TestProcessHost;

/// <summary>
/// Provides stable caller positions for property step-filtering integration tests.
/// </summary>
internal static class DebuggerStepFilteringFixture
{
    /// <summary>
    /// Reads one managed property and waits for the debugger to finish inspection.
    /// </summary>
    /// <param name="path">The signal file path that permits the fixture to exit.</param>
    /// <returns>Zero when the property produced the expected value.</returns>
    internal static int Run(string path)
    {
        var value = new DebuggerStepFilteringValue(41);
        int answer = value.Answer;
        while (!File.Exists(path))
        {
            Thread.SpinWait(10_000);
        }

        return answer == 42 ? 0 : 1;
    }
}
