namespace Csls.TestProcessHost;

/// <summary>
/// Provides a stable synchronous managed frame for debugger integration tests.
/// </summary>
internal static class DebuggerFixture
{
    /// <summary>
    /// Keeps named arguments and locals live until the test creates a signal file.
    /// </summary>
    /// <param name="path">The signal file path.</param>
    /// <param name="announcement">The readiness text written before waiting.</param>
    /// <param name="number">The numeric argument retained in the frame.</param>
    /// <param name="text">The string argument retained in the frame.</param>
    /// <returns>Zero when the retained local values remain intact.</returns>
    internal static int WaitForSignal(
        string path,
        string announcement,
        int number,
        string text)
    {
        int localNumber = number + 1;
        long localLong = number + 2L;
        byte localByte = 1;
        string localText = text + "!";
        int[] localArray = [41, 42, 43];
        var localObject = new DebuggerFixtureValue(number, localText, path + ".evaluation");
        Console.Write(announcement);
        Console.Out.Flush();
        while (!File.Exists(path))
        {
            Thread.SpinWait(10_000);
        }

        GC.KeepAlive(path);
        GC.KeepAlive(text);
        GC.KeepAlive(localLong);
        GC.KeepAlive(localByte);
        GC.KeepAlive(localArray);
        GC.KeepAlive(localObject);
        return localNumber == 43 && localText == "answer!" ? 0 : 1;
    }
}
