using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Csls.Debugger;

/// <summary>
/// Reaps a dbgshim-created Unix child and preserves its real exit status.
/// </summary>
internal static partial class UnixChildExitMonitor
{
    private const int InterruptedError = 4;

    /// <summary>
    /// Starts a dedicated blocking wait before the CoreCLR transport poller can reap the child.
    /// </summary>
    /// <param name="processId">The direct child process identifier returned by dbgshim.</param>
    /// <returns>A task containing the process exit code or signal-derived status.</returns>
    internal static Task<int> StartAsync(uint processId)
    {
        ArgumentOutOfRangeException.ThrowIfZero(processId);
        int checkedProcessId = checked((int)processId);
        return Task.Factory.StartNew(
            () => WaitForExit(checkedProcessId),
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);
    }

    private static int WaitForExit(int processId)
    {
        while (true)
        {
            int result = WaitProcess(processId, out int status, options: 0);
            if (result == processId)
            {
                return DecodeStatus(status);
            }

            int error = Marshal.GetLastPInvokeError();
            if (result < 0 && error == InterruptedError)
            {
                continue;
            }

            throw new Win32Exception(error, $"waitpid({processId}) failed.");
        }
    }

    private static int DecodeStatus(int status)
    {
        int terminationSignal = status & 0x7f;
        if (terminationSignal == 0)
        {
            return (status >> 8) & 0xff;
        }

        return terminationSignal == 0x7f ? 1 : 128 + terminationSignal;
    }

    [LibraryImport("libc", EntryPoint = "waitpid", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static partial int WaitProcess(int processId, out int status, int options);
}
