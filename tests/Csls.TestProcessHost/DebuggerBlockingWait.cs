using Microsoft.Win32.SafeHandles;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Csls.TestProcessHost;

/// <summary>
/// Announces readiness only after the inspected thread enters an unreleased runtime wait.
/// </summary>
internal static partial class DebuggerBlockingWait
{
    /// <summary>
    /// Holds the calling thread in a native wait until its owning test terminates the process.
    /// </summary>
    /// <param name="announcement">The readiness text emitted after the wait becomes observable.</param>
    internal static void Wait(string announcement)
    {
        if (OperatingSystem.IsWindows())
        {
            WaitOnWindows(announcement);
            return;
        }

        Thread inspectedThread = Thread.CurrentThread;
        using var gate = new ManualResetEvent(false);
        int enteringWait = 0;
        var observer = new Thread(() =>
        {
            // Thread.Start can itself wait; only observe the thread after startup has returned.
            SpinWait.SpinUntil(() => Volatile.Read(ref enteringWait) != 0);
            SpinWait.SpinUntil(() => (inspectedThread.ThreadState & ThreadState.WaitSleepJoin) != 0);
            HoldReadinessThread(gate, announcement);
        })
        {
            IsBackground = true
        };
        observer.Start();
        Volatile.Write(ref enteringWait, 1);
        // No thread releases this gate. Readiness cannot race a return into managed fixture code.
        _ = gate.WaitOne();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void WaitOnWindows(string announcement)
    {
        using var readiness = new EventWaitHandle(false, EventResetMode.ManualReset);
        using var gate = new EventWaitHandle(false, EventResetMode.ManualReset);
        var observer = new Thread(() =>
        {
            _ = readiness.WaitOne();
            HoldReadinessThread(gate, announcement);
        })
        {
            IsBackground = true
        };
        observer.Start();
        uint result = SignalObjectAndWait(
            readiness.SafeWaitHandle,
            gate.SafeWaitHandle,
            uint.MaxValue,
            alertable: 0);
        throw new Win32Exception(Marshal.GetLastPInvokeError(),
            $"SignalObjectAndWait unexpectedly returned 0x{result:X8}.");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void HoldReadinessThread(WaitHandle gate, string announcement)
    {
        Console.Write(announcement);
        Console.Out.Flush();
        // The collector enumerates threads after readiness; this thread must not exit during that enumeration.
        _ = gate.WaitOne();
    }

    [LibraryImport("kernel32", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static partial uint SignalObjectAndWait(
        SafeWaitHandle objectToSignal,
        SafeWaitHandle objectToWaitOn,
        uint milliseconds,
        int alertable);
}
