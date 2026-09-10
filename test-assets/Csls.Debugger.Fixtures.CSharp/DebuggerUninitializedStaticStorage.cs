namespace Csls.Debugger.Fixtures.CSharp;

/// <summary>
/// Defers static initialization until the debugger explicitly invokes target code.
/// </summary>
internal static class DebuggerUninitializedStaticStorage
{
    /// <summary>
    /// Holds a value derived from the thread that first initializes this type.
    /// </summary>
    internal static readonly int s_number = DebuggerStaticStorageFixture.s_threadNumber + 303;

    static DebuggerUninitializedStaticStorage()
    {
        DebuggerStaticStorageFixture.RecordInitialization();
    }

    /// <summary>
    /// Executes a normal static method and triggers the runtime's type-initialization contract.
    /// </summary>
    /// <returns>The initialized field's value.</returns>
    internal static int ReadNumber() => s_number;
}
