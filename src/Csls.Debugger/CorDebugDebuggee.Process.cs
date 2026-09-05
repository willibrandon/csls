using System.ComponentModel;
using System.Diagnostics;

namespace Csls.Debugger;

/// <summary>
/// Owns platform-specific managed debuggee process lifecycle operations.
/// </summary>
internal sealed partial class CorDebugDebuggee
{
    /// <inheritdoc />
    public async Task<int> WaitForExitAsync(CancellationToken cancellationToken)
    {
        using var observation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Task<int> exit = WaitForOperatingSystemExitAsync(observation.Token);
        Task<CorDebugRuntimeException> failure =
            _managedCallback.WaitForRuntimeFailureAsync(observation.Token);
        try
        {
            _ = await Task.WhenAny(exit, failure).ConfigureAwait(false);
            _managedCallback.ThrowIfRuntimeFailed();
            int exitCode = await exit.ConfigureAwait(false);
            await _managedCallback.WaitForExitProcessAsync(observation.Token).ConfigureAwait(false);
            _managedCallback.ThrowIfRuntimeFailed();
            return exitCode;
        }
        finally
        {
            await observation.CancelAsync().ConfigureAwait(false);
        }
    }

    private async Task<int> WaitForOperatingSystemExitAsync(CancellationToken cancellationToken)
    {
        int exitCode;
        if (_unixExitMonitor is not null)
        {
            int? monitoredExitCode = await _unixExitMonitor.WaitAsync(cancellationToken)
                .ConfigureAwait(false);
            exitCode = monitoredExitCode ?? GetExitCode(_process);
        }
        else
        {
            await _process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            exitCode = GetExitCode(_process);
        }

        return exitCode;
    }

    /// <inheritdoc />
    public Task TerminateAsync(CancellationToken cancellationToken) =>
        TerminateProcessAsync(_process, _unixExitMonitor, cancellationToken);

    private static int GetExitCode(Process process)
    {
        try
        {
            return process.ExitCode;
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or Win32Exception)
        {
            return 0;
        }
    }

    private static async Task TerminateProcessAsync(
        Process process,
        UnixChildExitMonitor? unixExitMonitor,
        CancellationToken cancellationToken)
    {
        if (unixExitMonitor is not null)
        {
            if (!unixExitMonitor.IsCompleted)
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (Exception exception) when (
                    exception is (InvalidOperationException or Win32Exception) &&
                    unixExitMonitor.IsCompleted)
                {
                    Debug.Assert(unixExitMonitor.IsCompleted);
                }
            }

            _ = await unixExitMonitor.WaitAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        if (!process.HasExited)
        {
            process.Kill(entireProcessTree: true);
        }

        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
    }
}
