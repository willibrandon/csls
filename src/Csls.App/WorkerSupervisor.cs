using Csls.Workspaces;
using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace Csls.App;

/// <summary>
/// Starts and supervises the managed Roslyn worker with inherited protocol streams.
/// </summary>
internal static class WorkerSupervisor
{
    private const string RuntimeHostPathEnvironmentVariable = "CSLS_RUNTIME_HOST_PATH";

    /// <summary>
    /// Runs the managed worker until it exits or the command is canceled.
    /// </summary>
    internal static async Task<int> RunAsync(CancellationToken cancellationToken)
    {
        string workerPath = WorkerLocator.Resolve();
        ProcessStartInfo startInfo = CreateStartInfo(workerPath);
        using Process process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("The csls Roslyn worker did not start.");
        var processTree = WindowsProcessTreeLifetime.Attach(process);
        await using ConfiguredAsyncDisposable processTreeCleanup =
            processTree.ConfigureAwait(false);

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

            await processTree.TerminateDescendantsAsync().ConfigureAwait(false);
            return 130;
        }

        await processTree.TerminateDescendantsAsync().ConfigureAwait(false);
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
        string? hostPath = Environment.GetEnvironmentVariable(
            RuntimeHostPathEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(hostPath))
        {
            return hostPath;
        }

        hostPath = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH");
        return string.IsNullOrWhiteSpace(hostPath) ? "dotnet" : hostPath;
    }
}
