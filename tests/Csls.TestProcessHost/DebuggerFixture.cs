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
    /// <param name="tupleArgument">The named tuple argument retained in the frame.</param>
    /// <returns>Zero when the retained local values remain intact.</returns>
    internal static int WaitForSignal(
        string path,
        string announcement,
        int number,
        string text,
        (int ArgumentNumber, string ArgumentText) tupleArgument)
    {
        int localNumber = number + 1;
        long localLong = number + 2L;
        byte localByte = 1;
        string localText = text + "!";
        int[] localArray = [41, 42, 43];
        var localObject = new DebuggerFixtureValue(number, localText, path + ".evaluation");
        var localList = new DebuggerFixtureList(number);
        var localBrowsable = new DebuggerBrowsableFixture();
        var localDisplay = new DebuggerDisplayFixture();
        var localDisplays = new DebuggerDisplayContainerFixture();
        var localProxy = new DebuggerTypeProxyFixture();
        var localGenericProxy = new GenericDebuggerTypeProxyFixture<int>(49);
        var localInheritedProxy = new InheritedDebuggerTypeProxyFixture();
        var localAssemblyProxy = new AssemblyDebuggerTypeProxyFixture();
        var localAssemblyProxyProjection = new AssemblyDebuggerTypeProxyFixtureProxy(
            localAssemblyProxy);
        var localAssemblyNamedProxy = new AssemblyNamedDebuggerTypeProxyFixture();
        var localAssemblyNamedProxyProjection =
            new AssemblyNamedDebuggerTypeProxyFixtureProxy(localAssemblyNamedProxy);
        var localArityMismatchProxy = new ArityMismatchDebuggerTypeProxyFixture<int>(51);
        var localArityMismatchProxyProjection =
            new ArityMismatchDebuggerTypeProxyFixtureProxy<int, string>(
                localArityMismatchProxy);
        var localThrowingProxy = new ThrowingDebuggerTypeProxyFixture();
        DebuggerDisplayFixture[] localDisplayArray = [new()];
        int? localNullable = number + 3;
        int? localEmptyNullable = null;
        (int Number, string Text) localTuple = (number, text);
        object localBoxedTuple = (10, "ten");
        (int One, int Two, int Three, int Four, int Five, int Six, int Seven,
            int Eight, int Nine) localLongTuple =
            (1, 2, 3, 4, 5, 6, 7, 8, 9);
        (int One, int Two, int Three, int Four, int Five, int Six, int Seven,
            int Eight) localEightTuple =
            (1, 2, 3, 4, 5, 6, 7, 8);
        (int One, int Two, int Three, int Four, int Five, int Six, int Seven,
            int Eight, int Nine, int Ten, int Eleven, int Twelve, int Thirteen,
            int Fourteen, int Fifteen, int Sixteen) localSixteenTuple =
            (1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16);
        ((int InnerNumber, string InnerText) Inner, int OuterNumber) localNestedTuple =
            ((number, text), number + 1);
        (int Number, string Text)[] localTupleArray = [(number, text)];
        ValueTuple<int> localSingleTuple = new(1);
        ValueTuple<int, int, int, int, int, int, int, int> localNonTuple = default;
        DebuggerFixtureMode localMode = DebuggerFixtureMode.Second;
        var localUnknownMode = (DebuggerFixtureMode)7;
        DebuggerFixtureOptions localOptions = DebuggerFixtureOptions.Read |
            DebuggerFixtureOptions.Execute;
        decimal localDecimal = -1234.50m;
        DateTime localDateTime = new DateTime(
            2026,
            9,
            3,
            12,
            34,
            56,
            DateTimeKind.Utc).AddTicks(7_890_123);
        var localUnspecifiedDateTime = new DateTime(
            2026,
            9,
            3,
            12,
            34,
            56,
            DateTimeKind.Unspecified);
        var localLocalDateTime = new DateTime(
            2026,
            9,
            3,
            12,
            34,
            56,
            DateTimeKind.Local);
        var localTimeSpan = new TimeSpan(1, 2, 3, 4, 500);
        var localGuid = new Guid("00112233-4455-6677-8899-aabbccddeeff");
        var localDateTimeOffset = new DateTimeOffset(
            2026,
            9,
            3,
            12,
            34,
            56,
            TimeSpan.FromHours(-7));
        string localEscapedText = "a\0\a\b\f\v\\\"\u0001😀";
        char localEscapedCharacter = '\n';
        Console.Write(announcement);
        Console.Out.Flush();
        while (!File.Exists(path))
        {
            Thread.Sleep(1);
        }

        GC.KeepAlive(path);
        GC.KeepAlive(text);
        GC.KeepAlive(tupleArgument);
        GC.KeepAlive(localLong);
        GC.KeepAlive(localByte);
        GC.KeepAlive(localArray);
        GC.KeepAlive(localObject);
        GC.KeepAlive(localList);
        GC.KeepAlive(localBrowsable);
        GC.KeepAlive(localDisplay);
        GC.KeepAlive(localDisplays);
        GC.KeepAlive(localProxy);
        GC.KeepAlive(localGenericProxy);
        GC.KeepAlive(localInheritedProxy);
        GC.KeepAlive(localAssemblyProxy);
        GC.KeepAlive(localAssemblyProxyProjection);
        GC.KeepAlive(localAssemblyNamedProxy);
        GC.KeepAlive(localAssemblyNamedProxyProjection);
        GC.KeepAlive(localArityMismatchProxy);
        GC.KeepAlive(localArityMismatchProxyProjection);
        GC.KeepAlive(localThrowingProxy);
        GC.KeepAlive(localDisplayArray);
        GC.KeepAlive(localNullable);
        GC.KeepAlive(localEmptyNullable);
        GC.KeepAlive(localTuple);
        GC.KeepAlive(localBoxedTuple);
        GC.KeepAlive(localLongTuple);
        GC.KeepAlive(localEightTuple);
        GC.KeepAlive(localSixteenTuple);
        GC.KeepAlive(localNestedTuple);
        GC.KeepAlive(localTupleArray);
        GC.KeepAlive(localSingleTuple);
        GC.KeepAlive(localNonTuple);
        GC.KeepAlive(localMode);
        GC.KeepAlive(localUnknownMode);
        GC.KeepAlive(localOptions);
        GC.KeepAlive(localDecimal);
        GC.KeepAlive(localDateTime);
        GC.KeepAlive(localUnspecifiedDateTime);
        GC.KeepAlive(localLocalDateTime);
        GC.KeepAlive(localTimeSpan);
        GC.KeepAlive(localGuid);
        GC.KeepAlive(localDateTimeOffset);
        GC.KeepAlive(localEscapedText);
        GC.KeepAlive(localEscapedCharacter);
        return localNumber == 43 && localText == "answer!" ? 0 : 1;
    }
}
