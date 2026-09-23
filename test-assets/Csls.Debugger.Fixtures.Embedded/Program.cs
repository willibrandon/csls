namespace Csls.Debugger.Fixtures.Embedded;

/// <summary>
/// Provides a stable managed frame whose symbols live inside the assembly.
/// </summary>
internal static class Program
{
    /// <summary>
    /// Retains a named local until the debugger test creates its signal file.
    /// </summary>
    /// <param name="args">The single signal-file path.</param>
    /// <returns>Zero when the local value remains intact.</returns>
    internal static int Main(string[] args)
    {
        ArgumentOutOfRangeException.ThrowIfNotEqual(args.Length, 1);
        int number = 41;
        int embeddedNumber = number + 1;
        while (!File.Exists(args[0]))
        {
            Thread.Sleep(1);
        }

        GC.KeepAlive(args);
        GC.KeepAlive(embeddedNumber);
        return 0;
    }
}
