namespace Csls.Debugger.Fixtures.CSharp;

/// <summary>
/// Holds independently observable per-thread storage and type-initialization counters.
/// </summary>
internal static class DebuggerStaticStorageFixture
{
    /// <summary>
    /// Holds a different value on each fixture thread.
    /// </summary>
    [ThreadStatic]
    internal static int s_threadNumber;

    /// <summary>
    /// Counts executions of the initially untouched type's static constructor.
    /// </summary>
    internal static int s_initializations;

    private static int s_workerObserved;

    /// <summary>
    /// Stops with both thread-local values initialized and reports the resumed target's observations.
    /// </summary>
    /// <param name="arguments">The test launch arguments used for output identification.</param>
    /// <returns>Zero after both threads complete.</returns>
    internal static int Run(string[] arguments)
    {
        s_threadNumber = 11;
        using var ready = new ManualResetEvent(false);
        using var release = new ManualResetEvent(false);
        var worker = new Thread(() => HoldWorker(ready, release)) { Name = "static-storage-worker" };
        worker.Start();
        try
        {
            ready.WaitOne();
            int sentinel = 42;
            Console.Write(arguments[0]);
            GC.KeepAlive(sentinel);
        }
        finally
        {
            release.Set();
            worker.Join();
        }
        Console.Write(FormattableString.Invariant($"{s_threadNumber}:{s_workerObserved}:{s_initializations}"));
        return 0;
    }

    /// <summary>
    /// Records a real runtime execution of the untouched type's initializer.
    /// </summary>
    internal static void RecordInitialization() => s_initializations++;

    private static void HoldWorker(EventWaitHandle ready, WaitHandle release)
    {
        s_threadNumber = 22;
        ready.Set();
        release.WaitOne();
        s_workerObserved = s_threadNumber;
    }
}
