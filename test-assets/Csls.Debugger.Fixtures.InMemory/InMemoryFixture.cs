namespace Csls.Debugger.Fixtures.InMemory;

/// <summary>
/// Provides stable source and local state for in-memory debugger integration coverage.
/// </summary>
public static class InMemoryFixture
{
    /// <summary>
    /// Executes a stable source statement and waits for the debugger test signal.
    /// </summary>
    /// <param name="signalPath">The file whose creation releases the fixture.</param>
    /// <returns>Zero after the stable local remains live.</returns>
    public static int WaitForSignal(string signalPath)
    {
        int answer = 41;
        answer++;
        while (!File.Exists(signalPath))
        {
            Thread.SpinWait(10_000);
        }

        return answer - 42;
    }
}
