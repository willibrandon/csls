using System.Runtime.CompilerServices;

namespace Csls.Debugger.Fixtures.CSharp;

/// <summary>
/// Keeps optimized storage lifetimes distinct while a real thread remains stopped in a native wait.
/// </summary>
internal static class UnavailableLocalsFixture
{
    /// <summary>
    /// Expires the first local before retaining the following local across a blocking call.
    /// </summary>
    /// <param name="seed">The value used before the blocking call.</param>
    /// <param name="retainedArgument">The argument kept live after the blocking call.</param>
    /// <returns>The sum of the retained local and argument after the wait.</returns>
    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
    internal static int Run(int seed, int retainedArgument)
    {
        int expired = seed + 1;
        while (expired < 0)
        {
            expired = ~expired;
        }

        Console.Write(expired);
        int retained = expired + 1;
        WaitForInspection();
        return (retained < 0 ? ~retained : retained) + retainedArgument;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void WaitForInspection()
    {
        Thread inspected = Thread.CurrentThread;
        using var gate = new ManualResetEvent(false);
        int enteringWait = 0;
        var observer = new Thread(() =>
        {
            SpinWait.SpinUntil(() => Volatile.Read(ref enteringWait) != 0);
            SpinWait.SpinUntil(() => (inspected.ThreadState & ThreadState.WaitSleepJoin) != 0);
            Console.Write("ready");
            Console.Out.Flush();
        })
        {
            IsBackground = true
        };
        observer.Start();
        Volatile.Write(ref enteringWait, 1);
        _ = gate.WaitOne();
    }
}
