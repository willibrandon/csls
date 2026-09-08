using System.Diagnostics;
using System.Text;

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
    /// <param name="diagnosticContext">Optionally captures the owned child's native stacks before cancellation cleanup.</param>
    /// <returns>The exit code, standard output, and standard error.</returns>
    internal static async Task<(int ExitCode, string Output, string Error)> RunAsync(
        ProcessStartInfo startInfo,
        CancellationToken cancellationToken,
        Action<string>? progress = null,
        TestContext? diagnosticContext = null)
    {
        (int _, int exitCode, string output, string error) = await RunWithIdentityAsync(
            startInfo, cancellationToken, progress, diagnosticContext).ConfigureAwait(false);
        return (exitCode, output, error);
    }

    /// <summary>
    /// Runs one redirected child and preserves its process identity for operating-system crash diagnostics.
    /// </summary>
    /// <param name="startInfo">The complete child-process start information.</param>
    /// <param name="cancellationToken">Cancels the process and terminates its complete tree.</param>
    /// <param name="progress">Optionally observes diagnostic lines while the child is still running.</param>
    /// <param name="diagnosticContext">Optionally captures the owned child's native stacks before cancellation cleanup.</param>
    /// <returns>The process identifier, exit code, standard output, and standard error.</returns>
    internal static async Task<(int ProcessId, int ExitCode, string Output, string Error)> RunWithIdentityAsync(
        ProcessStartInfo startInfo,
        CancellationToken cancellationToken,
        Action<string>? progress = null,
        TestContext? diagnosticContext = null)
    {
        ArgumentNullException.ThrowIfNull(startInfo);
        startInfo.RedirectStandardOutput = true;
        startInfo.RedirectStandardError = true;
        startInfo.UseShellExecute = false;
        using Process process = Process.Start(startInfo)
            ?? throw new InvalidOperationException(
                $"The debugger test process did not start: {startInfo.FileName}");
        Task<string> output = ReadOutputAsync(process.StandardOutput, progress, cancellationToken);
        Task<string> error = ReadOutputAsync(process.StandardError, progress, cancellationToken);
        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            return (
                process.Id,
                process.ExitCode,
                await output.ConfigureAwait(false),
                await error.ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            try
            {
                if (diagnosticContext is not null && !process.HasExited)
                {
                    await DebuggerProcessDiagnostics.CaptureAsync(process.Id, diagnosticContext).ConfigureAwait(false);
                }
            }
            finally
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

                Task streams = Task.WhenAll(output, error);
                await streams.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            }
            throw;
        }
    }

    private static async Task<string> ReadOutputAsync(StreamReader reader, Action<string>? progress,
        CancellationToken cancellationToken)
    {
        if (progress is null)
        {
            return await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        }

        var result = new StringBuilder();
        var line = new StringBuilder();
        char[] buffer = new char[4096];
        bool skipLineFeed = false;
        try
        {
            // Own partial lines so cancelling a read cannot discard an already captured diagnostic fragment.
            while (await reader.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false) is int count && count > 0)
            {
                for (int index = 0; index < count; index++)
                {
                    char character = buffer[index];
                    if (skipLineFeed)
                    {
                        skipLineFeed = false;
                        if (character == '\n')
                        {
                            continue;
                        }
                    }
                    if (character is '\r' or '\n')
                    {
                        PublishLine(result, line, progress);
                        skipLineFeed = character == '\r';
                    }
                    else
                    {
                        line.Append(character);
                    }
                }
            }
        }
        finally
        {
            if (line.Length != 0)
            {
                PublishLine(result, line, progress);
            }
        }

        return result.ToString();
    }

    private static void PublishLine(StringBuilder result, StringBuilder line, Action<string> progress)
    {
        string record = line.ToString();
        line.Clear();
        result.AppendLine(record);
        progress(record);
    }
}
