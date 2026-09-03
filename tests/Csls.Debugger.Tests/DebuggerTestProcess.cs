using System.Diagnostics;

namespace Csls.Debugger.Tests;

/// <summary>
/// Runs real debugger-test child processes with cancellation-safe tree ownership.
/// </summary>
internal static class DebuggerTestProcess
{
    /// <summary>
    /// Runs one redirected child process and returns its complete exit diagnostics.
    /// </summary>
    /// <param name="startInfo">The complete child-process start information.</param>
    /// <param name="cancellationToken">Cancels the process and terminates its complete tree.</param>
    /// <returns>The exit code, standard output, and standard error.</returns>
    internal static async Task<(int ExitCode, string Output, string Error)> RunAsync(
        ProcessStartInfo startInfo,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(startInfo);
        startInfo.RedirectStandardOutput = true;
        startInfo.RedirectStandardError = true;
        startInfo.UseShellExecute = false;
        using Process process = Process.Start(startInfo)
            ?? throw new InvalidOperationException(
                $"The debugger test process did not start: {startInfo.FileName}");
        Task<string> output = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
        Task<string> error = process.StandardError.ReadToEndAsync(CancellationToken.None);
        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (!process.HasExited)
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (InvalidOperationException) when (process.HasExited)
                {
                }

                await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            }

            _ = await Task.WhenAll(output, error).ConfigureAwait(false);
            throw;
        }

        return (
            process.ExitCode,
            await output.ConfigureAwait(false),
            await error.ConfigureAwait(false));
    }
}
