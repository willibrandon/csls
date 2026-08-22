using System.Diagnostics;

namespace Csls.App;

/// <summary>
/// Starts and supervises the managed Roslyn worker with inherited protocol streams.
/// </summary>
internal static class WorkerSupervisor
{
    /// <summary>
    /// Runs the managed worker until it exits or the command is canceled.
    /// </summary>
    internal static async Task<int> RunAsync(CancellationToken cancellationToken)
    {
        string workerPath = WorkerLocator.Resolve();
        ProcessStartInfo startInfo = CreateStartInfo(workerPath);
        using Process process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("The csls Roslyn worker did not start.");

        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            }

            return 130;
        }

        return process.ExitCode;
    }

    private static ProcessStartInfo CreateStartInfo(string workerPath)
    {
        bool isManagedAssembly = string.Equals(
            Path.GetExtension(workerPath),
            ".dll",
            StringComparison.OrdinalIgnoreCase);
        var startInfo = new ProcessStartInfo
        {
            FileName = isManagedAssembly ? ResolveDotNetHost() : workerPath,
            UseShellExecute = false
        };
        if (isManagedAssembly)
        {
            startInfo.ArgumentList.Add(workerPath);
        }

        return startInfo;
    }

    private static string ResolveDotNetHost()
    {
        string? hostPath = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH");
        return string.IsNullOrWhiteSpace(hostPath) ? "dotnet" : hostPath;
    }
}
