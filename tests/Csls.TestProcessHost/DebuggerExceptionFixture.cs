namespace Csls.TestProcessHost;

/// <summary>
/// Provides deterministic caught and unhandled managed exception events.
/// </summary>
internal static class DebuggerExceptionFixture
{
    /// <summary>
    /// Throws a caught exception and then waits so debugger tests retain process ownership.
    /// </summary>
    /// <param name="signalPath">The file whose creation releases the fixture.</param>
    /// <returns>Zero after the caught-exception path completes.</returns>
    internal static int Run(string signalPath)
    {
        try
        {
            throw new InvalidOperationException("debugger exception fixture");
        }
        catch (InvalidOperationException)
        {
            while (!File.Exists(signalPath))
            {
                Thread.Sleep(1);
            }
        }

        return 0;
    }
}
