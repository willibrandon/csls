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
    /// <param name="progress">Optionally observes diagnostic lines while the child is still running.</param>
    /// <returns>The exit code, standard output, and standard error.</returns>
    internal static async Task<(int ExitCode, string Output, string Error)> RunAsync(
        ProcessStartInfo startInfo,
        CancellationToken cancellationToken,
        Action<string>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(startInfo);
        startInfo.RedirectStandardOutput = true;
        startInfo.RedirectStandardError = true;
        startInfo.UseShellExecute = false;
        using Process process = Process.Start(startInfo)
            ?? throw new InvalidOperationException(
                $"The debugger test process did not start: {startInfo.FileName}");
        Task<string> output = ReadOutputAsync(process.StandardOutput, progress);
        Task<string> error = ReadOutputAsync(process.StandardError, progress);
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
                    await process.WaitForExitAsync(CancellationToken.None)
                        .ConfigureAwait(false);
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

    private static async Task<string> ReadOutputAsync(StreamReader reader, Action<string>? progress)
    {
        if (progress is null)
        {
            return await reader.ReadToEndAsync(CancellationToken.None).ConfigureAwait(false);
        }

        var result = new System.Text.StringBuilder();
        while (await reader.ReadLineAsync(CancellationToken.None).ConfigureAwait(false) is string line)
        {
            result.AppendLine(line);
            progress(line);
        }

        return result.ToString();
    }
}
