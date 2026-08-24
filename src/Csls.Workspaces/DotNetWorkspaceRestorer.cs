using System.Buffers;
using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace Csls.Workspaces;

/// <summary>
/// Restores discovered workspace entry points through a cancellable real .NET CLI process.
/// </summary>
internal static class DotNetWorkspaceRestorer
{
    private const int MaximumRetainedOutputCharacters = 32 * 1024;
    private const int ReadBufferCharacters = 4 * 1024;

    /// <summary>
    /// Restores every distinct entry point selected by bounded workspace discovery.
    /// </summary>
    /// <param name="workspaceRoots">The absolute roots of the live workspace.</param>
    /// <param name="cancellationToken">The operation cancellation token.</param>
    /// <returns>The number of restored solution or project entry points.</returns>
    internal static async Task<int> RestoreAsync(
        IReadOnlyList<string> workspaceRoots,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(workspaceRoots);
        string[] entryPoints =
        [
            .. workspaceRoots
                .SelectMany(root => WorkspaceDiscovery.Discover(root, cancellationToken))
                .Distinct(PathComparer)
                .Order(StringComparer.Ordinal)
        ];
        if (entryPoints.Length == 0)
        {
            throw new InvalidOperationException(
                "The current workspace contains no solution or project entry point to restore.");
        }

        foreach (string entryPoint in entryPoints)
        {
            await RestoreEntryPointAsync(entryPoint, cancellationToken).ConfigureAwait(false);
        }

        return entryPoints.Length;
    }

    private static StringComparer PathComparer => OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;

    private static async Task RestoreEntryPointAsync(
        string entryPoint,
        CancellationToken cancellationToken)
    {
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
        startInfo.ArgumentList.Add("--verbosity");
        startInfo.ArgumentList.Add("minimal");
        startInfo.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        startInfo.Environment["DOTNET_CLI_WORKLOAD_UPDATE_NOTIFY_DISABLE"] = "true";
        startInfo.Environment["DOTNET_NOLOGO"] = "true";

        using Process process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("The .NET restore process did not start.");
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
            if (TryKill(process))
            {
                await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
                await Task.WhenAll(standardOutput, standardError).ConfigureAwait(false);
            }

            throw;
        }

        string output = await standardOutput.ConfigureAwait(false);
        string error = await standardError.ConfigureAwait(false);
        if (process.ExitCode != 0)
        {
            string details = string.IsNullOrWhiteSpace(error) ? output : error;
            throw new InvalidOperationException(
                $"dotnet restore failed for {entryPoint} with exit code {process.ExitCode}: " +
                details.Trim());
        }
    }

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
