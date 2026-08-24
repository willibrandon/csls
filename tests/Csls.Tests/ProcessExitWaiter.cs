using System.Diagnostics;

namespace Csls.Tests;

/// <summary>
/// Waits for external test processes to release their operating-system resources.
/// </summary>
internal static class ProcessExitWaiter
{
    /// <summary>
    /// Waits for an observed process to exit, returning when it has already stopped.
    /// </summary>
    /// <param name="processId">The observed operating-system process identifier.</param>
    /// <param name="timeout">The maximum interval to wait for a clean exit.</param>
    /// <param name="cancellationToken">The test cancellation token.</param>
    /// <returns>A task that completes after the process exits.</returns>
    internal static async Task WaitAsync(
        int processId,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        Process process;
        try
        {
            process = Process.GetProcessById(processId);
        }
        catch (ArgumentException)
        {
            return;
        }

        using (process)
        {
            try
            {
                await process.WaitForExitAsync(cancellationToken)
                    .WaitAsync(timeout, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (TimeoutException exception)
            {
                throw new TimeoutException(
                    $"Process {processId} did not exit within {timeout}.",
                    exception);
            }
        }
    }
}
