using System.Buffers;
using System.Diagnostics;

namespace Csls.Cli.Worker;

/// <summary>
/// Runs external development tools without a command shell and bounds captured output.
/// </summary>
internal static class ExternalProcessRunner
{
    private const int MaximumCapturedCharacters = 64 * 1024;

    /// <summary>
    /// Runs one tool to completion with structured arguments and cancellation.
    /// </summary>
    /// <param name="fileName">The executable path or command name.</param>
    /// <param name="arguments">The ordered process arguments.</param>
    /// <param name="workingDirectory">The absolute process working directory.</param>
    /// <param name="cancellationToken">The process cancellation token.</param>
    /// <returns>The exit code and bounded output streams.</returns>
    internal static async Task<ExternalProcessResult> RunAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            WorkingDirectory = workingDirectory
        };
        foreach (string argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        startInfo.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        startInfo.Environment["DOTNET_CLI_WORKLOAD_UPDATE_NOTIFY_DISABLE"] = "true";
        startInfo.Environment["DOTNET_NOLOGO"] = "true";

        using Process process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"The {fileName} process did not start.");
        Task<(string Output, bool Truncated)> standardOutputTask = ReadBoundedAsync(
            process.StandardOutput,
            cancellationToken);
        Task<(string Output, bool Truncated)> standardErrorTask = ReadBoundedAsync(
            process.StandardError,
            cancellationToken);
        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            ValueTask<(string Output, bool Truncated)> standardOutputCompletion =
                new(standardOutputTask);
            await ObserveCanceledOutputAsync(
                standardOutputCompletion,
                cancellationToken).ConfigureAwait(false);
            ValueTask<(string Output, bool Truncated)> standardErrorCompletion =
                new(standardErrorTask);
            await ObserveCanceledOutputAsync(
                standardErrorCompletion,
                cancellationToken).ConfigureAwait(false);
            throw;
        }

        (string standardOutput, bool standardOutputTruncated) =
            await standardOutputTask.ConfigureAwait(false);
        (string standardError, bool standardErrorTruncated) =
            await standardErrorTask.ConfigureAwait(false);
        return new ExternalProcessResult
        {
            ExitCode = process.ExitCode,
            StandardOutput = standardOutput,
            StandardError = standardError,
            OutputTruncated = standardOutputTruncated || standardErrorTruncated
        };
    }

    private static async ValueTask ObserveCanceledOutputAsync(
        ValueTask<(string Output, bool Truncated)> completion,
        CancellationToken cancellationToken)
    {
        try
        {
            await completion.ConfigureAwait(false);
        }
        catch (Exception exception) when (
            cancellationToken.IsCancellationRequested &&
            exception is OperationCanceledException or IOException or ObjectDisposedException)
        {
            return;
        }
    }

    private static async Task<(string Output, bool Truncated)> ReadBoundedAsync(
        StreamReader reader,
        CancellationToken cancellationToken)
    {
        char[] buffer = ArrayPool<char>.Shared.Rent(4096);
        char[] captured = ArrayPool<char>.Shared.Rent(MaximumCapturedCharacters);
        try
        {
            int capturedCount = 0;
            bool truncated = false;
            int read;
            while ((read = await reader
                .ReadAsync(buffer.AsMemory(0, 4096), cancellationToken)
                .ConfigureAwait(false)) > 0)
            {
                int remaining = MaximumCapturedCharacters - capturedCount;
                int copyCount = Math.Min(read, remaining);
                if (copyCount > 0)
                {
                    buffer.AsSpan(0, copyCount).CopyTo(captured.AsSpan(capturedCount));
                    capturedCount += copyCount;
                }

                truncated |= copyCount < read;
            }

            return (new string(captured, 0, capturedCount), truncated);
        }
        finally
        {
            ArrayPool<char>.Shared.Return(buffer);
            ArrayPool<char>.Shared.Return(captured);
        }
    }
}
