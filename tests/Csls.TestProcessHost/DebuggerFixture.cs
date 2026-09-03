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
        var localList = new DebuggerFixtureList(number);
        int? localNullable = number + 3;
        int? localEmptyNullable = null;
        (int Number, string Text) localTuple = (number, text);
        DebuggerFixtureMode localMode = DebuggerFixtureMode.Second;
        var localUnknownMode = (DebuggerFixtureMode)7;
        DebuggerFixtureOptions localOptions = DebuggerFixtureOptions.Read |
            DebuggerFixtureOptions.Execute;
        decimal localDecimal = -1234.50m;
        string localEscapedText = "a\0\a\b\f\v\\\"\u0001😀";
        char localEscapedCharacter = '\n';
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
        GC.KeepAlive(localList);
        GC.KeepAlive(localNullable);
        GC.KeepAlive(localEmptyNullable);
        GC.KeepAlive(localTuple);
        GC.KeepAlive(localMode);
        GC.KeepAlive(localUnknownMode);
        GC.KeepAlive(localOptions);
        GC.KeepAlive(localDecimal);
        GC.KeepAlive(localEscapedText);
        GC.KeepAlive(localEscapedCharacter);
        return localNumber == 43 && localText == "answer!" ? 0 : 1;
    }
}
