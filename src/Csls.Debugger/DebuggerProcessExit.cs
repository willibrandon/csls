using System.Diagnostics;

namespace Csls.Debugger;

/// <summary>
/// Observes operating-system process completion for debugger-owned targets and workers.
/// </summary>
public static class DebuggerProcessExit
{
    /// <summary>
    /// Waits for process resource release while cancellation ends only the caller's observation.
    /// </summary>
    /// <param name="process">The associated process whose handle remains available when observation starts.</param>
    /// <param name="cancellationToken">Cancels observation without terminating the process.</param>
    /// <returns>A task completed after the operating system signals process termination.</returns>
    public static async Task WaitAsync(Process process, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(process);
        cancellationToken.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsWindows())
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        using var handle = new WindowsProcessExitWaitHandle(process.SafeHandle);
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        RegisteredWaitHandle registration = ThreadPool.RegisterWaitForSingleObject(handle,
            (_, _) => completion.TrySetResult(), state: null, Timeout.InfiniteTimeSpan, executeOnlyOnce: true);
        try
        {
            using CancellationTokenRegistration cancellation = cancellationToken.Register(
                () => completion.TrySetCanceled(cancellationToken));
            await completion.Task.ConfigureAwait(false);
        }
        finally
        {
            _ = registration.Unregister(null);
        }
    }
}
