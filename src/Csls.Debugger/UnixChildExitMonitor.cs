using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Csls.Debugger;

/// <summary>
/// Reaps a dbgshim-created Unix child and preserves its real exit status.
/// </summary>
internal sealed partial class UnixChildExitMonitor
{
    private const int InterruptedError = 4;
    private const int NoChildProcessError = 10;
    private const int NoHang = 1;
    private readonly Task<int?> _exitCode;

    private UnixChildExitMonitor(int processId)
    {
        UnixWaitStatusInterposer.Track(processId);
        using var ownershipReady = new ManualResetEventSlim();
        _exitCode = Task.Factory.StartNew(
            () => WaitForExit(processId, ownershipReady),
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);
        ownershipReady.Wait();
    }

    /// <summary>
    /// Starts a dedicated blocking wait before the CoreCLR transport poller can reap the child.
    /// </summary>
    /// <param name="processId">The direct child process identifier returned by dbgshim.</param>
    /// <returns>The sole child-reaping owner.</returns>
    internal static UnixChildExitMonitor Start(uint processId)
    {
        ArgumentOutOfRangeException.ThrowIfZero(processId);
        return new UnixChildExitMonitor(checked((int)processId));
    }

    /// <summary>
    /// Gets whether the child has already been reaped.
    /// </summary>
    internal bool IsCompleted => _exitCode.IsCompleted;

    /// <summary>
    /// Waits for the owned child and returns its decoded exit status.
    /// </summary>
    /// <param name="cancellationToken">Cancels waiting without abandoning child ownership.</param>
    /// <returns>The process exit code, signal status, or null if another native waiter won ownership.</returns>
    internal Task<int?> WaitAsync(CancellationToken cancellationToken) =>
        _exitCode.WaitAsync(cancellationToken);

    private static int? WaitForExit(int processId, ManualResetEventSlim ownershipReady)
    {
        bool preflightComplete = false;
        try
        {
            while (true)
            {
                int result = WaitProcess(
                    processId,
                    out int status,
                    preflightComplete ? 0 : NoHang);
                if (result == processId)
                {
                    return DecodeStatus(status);
                }

                int error = Marshal.GetLastPInvokeError();
                if (result < 0 && error == InterruptedError)
                {
                    continue;
                }

                if (result < 0 && error == NoChildProcessError)
                {
                    return UnixWaitStatusInterposer.TryGetExitCode(processId, out int exitCode)
                        ? exitCode
                        : null;
                }

                if (!preflightComplete && result == 0)
                {
                    preflightComplete = true;
                    ownershipReady.Set();
                    continue;
                }

                throw new Win32Exception(error, $"waitpid({processId}) failed.");
            }
        }
        finally
        {
            if (!preflightComplete)
            {
                ownershipReady.Set();
            }
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
