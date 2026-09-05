namespace Csls.TestProcessHost;

/// <summary>
/// Announces readiness only after the inspected thread enters an unreleased runtime wait.
/// </summary>
internal static class DebuggerBlockingWait
{
    /// <summary>
    /// Holds the calling thread in a native wait until its owning test terminates the process.
    /// </summary>
    /// <param name="announcement">The readiness text emitted after the wait becomes observable.</param>
    internal static void Wait(string announcement)
    {
        Thread inspectedThread = Thread.CurrentThread;
        using var gate = new ManualResetEvent(false);
        int enteringWait = 0;
        var observer = new Thread(() =>
        {
            // Thread.Start can itself wait; only observe the thread after startup has returned.
            SpinWait.SpinUntil(() => Volatile.Read(ref enteringWait) != 0);
            SpinWait.SpinUntil(() => (inspectedThread.ThreadState & ThreadState.WaitSleepJoin) != 0);
            Console.Write(announcement);
            Console.Out.Flush();
        })
        {
            IsBackground = true
        };
        observer.Start();
        Volatile.Write(ref enteringWait, 1);
        // No thread releases this gate. Readiness cannot race a return into managed fixture code.
        _ = gate.WaitOne();
    }
}
