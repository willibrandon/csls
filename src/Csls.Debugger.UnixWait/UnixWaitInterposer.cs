using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Csls.Debugger.UnixWait;

/// <summary>
/// Preserves exact child status when CoreCLR's Unix transport poller calls waitpid.
/// </summary>
internal static partial class UnixWaitInterposer
{
    private const int StoppedSignal = 0x7f;
    private static int s_processId;
    private static int s_exitCode;
    private static int s_hasExitCode;
    private static nint s_waitProcess;

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
        Volatile.Write(ref s_hasExitCode, 0);
        Volatile.Write(ref s_exitCode, 0);
        Volatile.Write(ref s_processId, processId);
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
        if (exitCode is null || Volatile.Read(ref s_processId) != processId ||
            Volatile.Read(ref s_hasExitCode) == 0)
        {
            return 0;
        }

        *exitCode = Volatile.Read(ref s_exitCode);
        return 1;
    }

    /// <summary>
    /// Interposes waitpid and records status without changing libc behavior.
    /// </summary>
    /// <param name="processId">The child selection accepted by waitpid.</param>
    /// <param name="status">Receives the native wait status.</param>
    /// <param name="options">The native wait options.</param>
    /// <returns>The result returned by libc waitpid.</returns>
    [UnmanagedCallersOnly(
        EntryPoint = "waitpid",
        CallConvs = [typeof(CallConvCdecl)])]
    internal static unsafe int WaitProcess(int processId, int* status, int options)
    {
        nint waitProcess = Interlocked.CompareExchange(ref s_waitProcess, 0, 0);
        if (waitProcess == 0)
        {
            ReadOnlySpan<byte> symbol = "waitpid\0"u8;
            fixed (byte* symbolPointer = symbol)
            {
                waitProcess = ResolveNext(new nint(-1), symbolPointer);
            }

            if (waitProcess == 0)
            {
                Environment.FailFast("The csls debugger could not resolve libc waitpid.");
            }

            nint previous = Interlocked.CompareExchange(
                ref s_waitProcess,
                waitProcess,
                0);
            if (previous != 0)
            {
                waitProcess = previous;
            }
        }

        var waitProcessFunction =
            (delegate* unmanaged[Cdecl]<int, int*, int, int>)waitProcess;
        int result = waitProcessFunction(processId, status, options);
        int error = Marshal.GetLastPInvokeError();
        if (result > 0 && status is not null && result == Volatile.Read(ref s_processId))
        {
            int exitCode = DecodeStatus(*status);
            if (exitCode >= 0)
            {
                Volatile.Write(ref s_exitCode, exitCode);
                Volatile.Write(ref s_hasExitCode, 1);
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

    [LibraryImport("libdl", EntryPoint = "dlsym")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static unsafe partial nint ResolveNext(nint handle, byte* symbol);
}
