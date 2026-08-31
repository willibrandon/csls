using System.Diagnostics;

namespace Csls.Tests;

/// <summary>
/// Waits for external test processes to release their operating-system resources.
/// </summary>
internal static class ProcessExitWaiter
{
    /// <summary>
    /// Captures the exact process instance before the action expected to stop it.
    /// </summary>
    /// <param name="processId">The observed operating-system process identifier.</param>
    /// <returns>An exit observation registered for the exact process instance.</returns>
    internal static ProcessExitObservation Observe(int processId) =>
        new(processId, ObserveExitAsync(processId));

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
        await WaitAsync(
            Observe(processId),
            timeout,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Waits for an exact previously observed process instance to exit.
    /// </summary>
    /// <param name="observation">The exact process exit observation.</param>
    /// <param name="timeout">The maximum interval to wait for a clean exit.</param>
    /// <param name="cancellationToken">The test cancellation token.</param>
    /// <returns>A task that completes after the process exits.</returns>
    internal static async Task WaitAsync(
        ProcessExitObservation observation,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        try
        {
            await observation.ExitTask.WaitAsync(timeout, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (TimeoutException exception)
        {
            throw new TimeoutException(
                $"Process {observation.ProcessId} did not exit within {timeout}.",
                exception);
        }
    }

    private static async Task ObserveExitAsync(int processId)
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
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
        }
    }
}
