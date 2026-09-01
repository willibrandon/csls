using Microsoft.Extensions.Logging;
using System.Buffers;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;

namespace Csls.Workspaces;

/// <summary>
/// Restores discovered workspace entry points through a cancellable real .NET CLI process.
/// </summary>
internal static partial class DotNetWorkspaceRestorer
{
    private const int MaximumRetainedOutputCharacters = 32 * 1024;
    private const int ReadBufferCharacters = 4 * 1024;

    /// <summary>
    /// Restores every distinct entry point selected by bounded workspace discovery.
    /// </summary>
    /// <param name="entryPoints">The discovered solution, project, or file-based app paths.</param>
    /// <param name="logger">The workspace restore logger.</param>
    /// <param name="cancellationToken">The operation cancellation token.</param>
    /// <returns>The number of restored solution or project entry points.</returns>
    internal static async Task<int> RestoreAsync(
        IReadOnlyList<string> entryPoints,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entryPoints);
        ArgumentNullException.ThrowIfNull(logger);
        if (entryPoints.Count == 0)
        {
            throw new InvalidOperationException(
                "The current workspace contains no solution or project entry point to restore.");
        }

        await Parallel.ForEachAsync(
            entryPoints,
            new ParallelOptions
            {
                CancellationToken = cancellationToken,
                MaxDegreeOfParallelism = Environment.ProcessorCount
            },
            async (entryPoint, restoreCancellationToken) =>
                await RestoreEntryPointAsync(
                    entryPoint,
                    logger,
                    restoreCancellationToken).ConfigureAwait(false)).ConfigureAwait(false);

        return entryPoints.Count;
    }

    private static async Task RestoreEntryPointAsync(
        string entryPoint,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        long startedTimestamp = Stopwatch.GetTimestamp();
        LogRestoreStarted(logger, entryPoint);
        string? configuredDotNetHost = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH");
        var startInfo = new ProcessStartInfo
        {
            FileName = string.IsNullOrWhiteSpace(configuredDotNetHost)
                ? "dotnet"
                : configuredDotNetHost,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(entryPoint)
                ?? throw new InvalidDataException($"Workspace entry point has no parent: {entryPoint}")
        };
        startInfo.ArgumentList.Add("restore");
        startInfo.ArgumentList.Add(entryPoint);
        startInfo.ArgumentList.Add("--nologo");
        startInfo.ArgumentList.Add("--disable-build-servers");
        startInfo.ArgumentList.Add("--property");
        startInfo.ArgumentList.Add("EnableWindowsTargeting=true");
        startInfo.ArgumentList.Add("--verbosity");
        startInfo.ArgumentList.Add("minimal");
        startInfo.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        startInfo.Environment["DOTNET_CLI_WORKLOAD_UPDATE_NOTIFY_DISABLE"] = "true";
        startInfo.Environment["DOTNET_NOLOGO"] = "true";

        using Process process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("The .NET restore process did not start.");
        var processTree = WindowsProcessTreeLifetime.Attach(process);
        await using ConfiguredAsyncDisposable processTreeCleanup =
            processTree.ConfigureAwait(false);
        Task<string> standardOutput = ReadBoundedAsync(process.StandardOutput);
        Task<string> standardError = ReadBoundedAsync(process.StandardError);
        using CancellationTokenRegistration cancellationRegistration = cancellationToken.Register(
            static state => TryKill((Process)state!),
            process);
        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _ = TryKill(process);
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            await processTree.TerminateDescendantsAsync().ConfigureAwait(false);
            await Task.WhenAll(standardOutput, standardError).ConfigureAwait(false);

            throw;
        }

        await processTree.TerminateDescendantsAsync().ConfigureAwait(false);
        string output = await standardOutput.ConfigureAwait(false);
        string error = await standardError.ConfigureAwait(false);
        if (process.ExitCode != 0)
        {
            string details = string.IsNullOrWhiteSpace(error) ? output : error;
            throw new InvalidOperationException(
                $"dotnet restore failed for {entryPoint} with exit code {process.ExitCode}: " +
                details.Trim());
        }

        long elapsedMilliseconds =
            (long)Stopwatch.GetElapsedTime(startedTimestamp).TotalMilliseconds;
        LogRestoreCompleted(logger, entryPoint, elapsedMilliseconds);
    }

    [LoggerMessage(
        EventId = 1,
        Level = LogLevel.Information,
        Message = "Restoring {EntryPoint}")]
    private static partial void LogRestoreStarted(ILogger logger, string entryPoint);

    [LoggerMessage(
        EventId = 2,
        Level = LogLevel.Information,
        Message = "Restored {EntryPoint} in {ElapsedMilliseconds} ms")]
    private static partial void LogRestoreCompleted(
        ILogger logger,
        string entryPoint,
        long elapsedMilliseconds);

    private static async Task<string> ReadBoundedAsync(StreamReader reader)
    {
        char[] buffer = ArrayPool<char>.Shared.Rent(ReadBufferCharacters);
        try
        {
            var retained = new StringBuilder(MaximumRetainedOutputCharacters);
            int read;
            while ((read = await reader.ReadAsync(buffer).ConfigureAwait(false)) != 0)
            {
                int remaining = MaximumRetainedOutputCharacters - retained.Length;
                if (remaining > 0)
                {
                    retained.Append(buffer, 0, Math.Min(read, remaining));
                }
            }

            return retained.ToString();
        }
        finally
        {
            ArrayPool<char>.Shared.Return(buffer);
        }
    }

    private static bool TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }

            return true;
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or Win32Exception or NotSupportedException)
        {
            return false;
        }
    }
}
