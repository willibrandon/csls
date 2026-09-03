namespace Csls.TestProcessHost;

/// <summary>
/// Produces two caught exception types for deterministic filter tests.
/// </summary>
internal static class DebuggerExceptionFilterFixture
{
    /// <summary>
    /// Throws a derived exception followed by a distinct exact exception type.
    /// </summary>
    /// <param name="signalPath">The file whose creation releases the fixture.</param>
    /// <returns>Zero after the second caught-exception path completes.</returns>
    internal static int Run(string signalPath)
    {
        try
        {
            throw new ArgumentException("base exception filter fixture");
        }
        catch (ArgumentException exception)
        {
            System.Diagnostics.Debug.Assert(exception.Message.Length > 0);
        }

        try
        {
            throw new InvalidOperationException("exact exception filter fixture");
        }
        catch (InvalidOperationException)
        {
            while (!File.Exists(signalPath))
            {
                Thread.SpinWait(10_000);
            }
        }

        return 0;
    }
}
