namespace Csls.Debugger.Fixtures.CSharp;

/// <summary>
/// Keeps a null namespace-shadowing local alive during static receiver selection tests.
/// </summary>
internal static class DebuggerStaticReceiverFixture
{
    /// <summary>
    /// Holds a real local whose source name shadows the namespace of loaded runtime types.
    /// </summary>
    /// <param name="arguments">The test launch arguments retaining a nonconstant null initialization.</param>
    /// <returns>The exact sentinel returned after debugger observation.</returns>
    internal static int Run(string[] arguments)
    {
        object? System = arguments.Length > 100 ? new object() : null;
        int sentinel = 42;
        Console.Write(arguments[0]);
        GC.KeepAlive(System);
        return sentinel;
    }
}
