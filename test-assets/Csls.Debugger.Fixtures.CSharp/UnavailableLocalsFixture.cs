using Microsoft.Win32.SafeHandles;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Csls.Debugger.Fixtures.CSharp;

/// <summary>
/// Keeps optimized storage lifetimes distinct while a real thread remains stopped in a native wait.
/// </summary>
internal static partial class UnavailableLocalsFixture
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
        if (OperatingSystem.IsWindows())
        {
            WaitForInspectionOnWindows();
            return;
        }

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

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void WaitForInspectionOnWindows()
    {
        using var readiness = new EventWaitHandle(false, EventResetMode.ManualReset);
        using var gate = new EventWaitHandle(false, EventResetMode.ManualReset);
        var observer = new Thread(() =>
        {
            _ = readiness.WaitOne();
            WriteReadinessAndWait(gate, "ready");
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
    private static void WriteReadinessAndWait(WaitHandle gate, string announcement)
    {
        Console.Write(announcement);
        Console.Out.Flush();
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
