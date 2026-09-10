using System.Runtime.CompilerServices;

namespace Csls.TestProcessHost;

/// <summary>
/// Creates a measured recursive managed stack on an explicitly sized target thread.
/// </summary>
internal static class DebuggerDeepStackFixture
{
    /// <summary>
    /// Descends to the requested depth and validates that every activation returns normally.
    /// </summary>
    /// <param name="depth">The number of simultaneously active recursive calls.</param>
    /// <returns>Zero when the complete stack unwinds with the expected result.</returns>
    internal static int Run(int depth)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(depth, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(depth, 100000);
        int result = 0;
        var thread = new Thread(() => result = Descend(depth - 1, 1), 32 * 1024 * 1024);
        thread.Start();
        thread.Join();
        return result == depth ? 0 : 1;
    }

    /// <summary>
    /// Exhausts only the fixture thread stack so the debugger must survive a fatal target event.
    /// </summary>
    /// <returns>A failure code if the recursive thread unexpectedly completes.</returns>
    internal static int RunOverflow()
    {
        var thread = new Thread(RunOverflowThread);
        thread.Start();
        thread.Join();
        return 1;
    }

    private static void RunOverflowThread()
    {
        DebuggerStackGuarantee.ReserveForOverflow();
        RuntimeHelpers.EnsureSufficientExecutionStack();
        Console.Out.WriteLine("overflow-ready");
        _ = Overflow(1);
    }

    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.NoOptimization)]
    private static int Overflow(int entered)
    {
        int descendants = Overflow(entered + 1);
        return descendants + 1;
    }

    /// <summary>
    /// Provides a target-executed operation for frame reacquisition probes.
    /// </summary>
    /// <param name="value">The value to increment.</param>
    /// <returns>The incremented value.</returns>
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static int AddOne(int value) => value + 1;

    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.NoOptimization)]
    private static int Descend(int remaining, int entered)
    {
        if (remaining == 0)
        {
            Console.Write($"depth:{entered}");
            return CompleteDescent(entered);
        }

        int descendants = Descend(remaining - 1, entered + 1);
        return descendants + 1;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int CompleteDescent(int entered) => entered > 0 ? 1 : 0;
}
