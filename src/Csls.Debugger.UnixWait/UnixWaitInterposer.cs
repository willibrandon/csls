using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Csls.Debugger.UnixWait;

/// <summary>
/// Preserves exact child status when CoreCLR's Unix transport poller calls waitpid.
/// </summary>
internal static partial class UnixWaitInterposer
{
    private const int NoHang = 1;
    private const int StoppedSignal = 0x7f;
    private static readonly Lock s_waitGate = new();
    private static readonly ManualResetEventSlim s_waitStatusReady = new();
    private static int s_processId;
    private static int s_exitCode;
    private static int s_hasExitCode;
    private static int s_waitStatus;

    /// <summary>
    /// Initializes the interposer before the debugger creates its first child process.
    /// </summary>
    [UnmanagedCallersOnly(
        EntryPoint = "csls_waitpid_initialize",
        CallConvs = [typeof(CallConvCdecl)])]
    internal static void Initialize()
    {
    }

    /// <summary>
    /// Selects the debugger-owned child whose terminal status must be retained.
    /// </summary>
    /// <param name="processId">The positive direct-child process identifier.</param>
    [UnmanagedCallersOnly(
        EntryPoint = "csls_waitpid_track",
        CallConvs = [typeof(CallConvCdecl)])]
    internal static void Track(int processId)
    {
        lock (s_waitGate)
        {
            s_waitStatusReady.Reset();
            Volatile.Write(ref s_hasExitCode, 0);
            Volatile.Write(ref s_exitCode, 0);
            Volatile.Write(ref s_waitStatus, 0);
            Volatile.Write(ref s_processId, processId);
        }
    }

    /// <summary>
    /// Returns the retained terminal status when the interposed waiter reaped the child.
    /// </summary>
    /// <param name="processId">The expected direct-child process identifier.</param>
    /// <param name="exitCode">Receives the decoded process or signal exit code.</param>
    /// <returns>One when an exact status is available; otherwise zero.</returns>
    [UnmanagedCallersOnly(
        EntryPoint = "csls_waitpid_try_get_exit_code",
        CallConvs = [typeof(CallConvCdecl)])]
    internal static unsafe int TryGetExitCode(int processId, int* exitCode)
    {
        if (exitCode is null)
        {
            return 0;
        }

        lock (s_waitGate)
        {
            if (Volatile.Read(ref s_processId) != processId ||
                Volatile.Read(ref s_hasExitCode) == 0)
            {
                return 0;
            }

            *exitCode = Volatile.Read(ref s_exitCode);
            return 1;
        }
    }

    /// <summary>
    /// Preserves the tracked child's status for native waitpid consumers.
    /// </summary>
    /// <param name="processId">The child selection accepted by waitpid.</param>
    /// <param name="status">Receives the native wait status.</param>
    /// <param name="options">The native wait options.</param>
    /// <returns>The result returned by libc waitpid.</returns>
    [UnmanagedCallersOnly(
#if CSLS_MACHO
        EntryPoint = "csls_waitpid_interposed",
#else
        EntryPoint = "waitpid",
#endif
        CallConvs = [typeof(CallConvCdecl)])]
    internal static unsafe int WaitProcess(int processId, int* status, int options) =>
        WaitProcessView(processId, status, options, nonCancelable: false);

#if CSLS_MACHO
    /// <summary>
    /// Preserves retained status for macOS runtime consumers using noncancelable waits.
    /// </summary>
    /// <param name="processId">The native child selection.</param>
    /// <param name="status">Receives the native wait status.</param>
    /// <param name="options">The native wait options.</param>
    /// <returns>The selected child's retained status or the native wait result.</returns>
    [UnmanagedCallersOnly(
        EntryPoint = "csls_waitpid_interposed_nocancel",
        CallConvs = [typeof(CallConvCdecl)])]
    internal static unsafe int WaitNonCancelableProcess(int processId, int* status, int options) =>
        WaitProcessView(processId, status, options, nonCancelable: true);
#endif

    private static unsafe int WaitProcessView(int processId, int* status, int options, bool nonCancelable)
    {
        if (processId != Volatile.Read(ref s_processId))
        {
            return WaitProcessCore(processId, status, options, nonCancelable);
        }

        if ((options & NoHang) == 0)
        {
            s_waitStatusReady.Wait();
        }

        lock (s_waitGate)
        {
            if (Volatile.Read(ref s_hasExitCode) == 0)
            {
                return 0;
            }

            if (status is not null)
            {
                *status = Volatile.Read(ref s_waitStatus);
            }

            return processId;
        }
    }

    /// <summary>
    /// Reaps the selected child for the debugger's sole managed exit monitor.
    /// </summary>
    /// <param name="processId">The exact debugger-owned child process identifier.</param>
    /// <param name="status">Receives the native wait status.</param>
    /// <param name="options">The native wait options.</param>
    /// <returns>The result returned by libc waitpid.</returns>
    [UnmanagedCallersOnly(
        EntryPoint = "csls_waitpid_wait",
        CallConvs = [typeof(CallConvCdecl)])]
    internal static unsafe int WaitTrackedProcess(int processId, int* status, int options) =>
        WaitProcessCore(processId, status, options, nonCancelable: false);

    private static unsafe int WaitProcessCore(int processId, int* status, int options, bool nonCancelable)
    {
        int result = nonCancelable && OperatingSystem.IsMacOS()
            ? WaitNonCancelableProcessNative(processId, status, options, nint.Zero)
            : WaitProcessNative(processId, status, options, nint.Zero);
        int error = Marshal.GetLastPInvokeError();
        if (result > 0 && status is not null && result == Volatile.Read(ref s_processId))
        {
            int exitCode = DecodeStatus(*status);
            if (exitCode >= 0)
            {
                lock (s_waitGate)
                {
                    Volatile.Write(ref s_waitStatus, *status);
                    Volatile.Write(ref s_exitCode, exitCode);
                    Volatile.Write(ref s_hasExitCode, 1);
                }

                s_waitStatusReady.Set();
            }
        }

        Marshal.SetLastPInvokeError(error);
        return result;
    }

    private static int DecodeStatus(int status)
    {
        int terminationSignal = status & 0x7f;
        if (terminationSignal == 0)
        {
            return (status >> 8) & 0xff;
        }

        return terminationSignal == StoppedSignal ? -1 : 128 + terminationSignal;
    }

    // A distinct libc entry point avoids dyld redirecting a dynamically resolved
    // owner wait back into the interposed consumer view.
    [LibraryImport("libc", EntryPoint = "wait4", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static unsafe partial int WaitProcessNative(
        int processId,
        int* status,
        int options,
        nint resourceUsage);

    [LibraryImport("libc", EntryPoint = "__wait4_nocancel", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static unsafe partial int WaitNonCancelableProcessNative(
        int processId,
        int* status,
        int options,
        nint resourceUsage);
}
