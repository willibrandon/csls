using System.Diagnostics;

namespace Csls.App;

/// <summary>
/// Starts and supervises the managed control worker with inherited terminal streams.
/// </summary>
internal static class CliWorkerSupervisor
{
    /// <summary>
    /// Runs one normalized CLI operation until it exits or the command is canceled.
    /// </summary>
    /// <param name="arguments">The normalized internal worker arguments.</param>
    /// <param name="cancellationToken">The launcher cancellation token.</param>
    /// <returns>The CLI worker exit code.</returns>
    internal static async Task<int> RunAsync(
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        string workerPath = CliWorkerLocator.Resolve();
        ProcessStartInfo startInfo = CreateStartInfo(workerPath, arguments);
        using Process process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("The csls CLI worker did not start.");

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

    private static ProcessStartInfo CreateStartInfo(
        string workerPath,
        IReadOnlyList<string> arguments)
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

        foreach (string argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        return startInfo;
    }

    private static string ResolveDotNetHost()
    {
        string? hostPath = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH");
        return string.IsNullOrWhiteSpace(hostPath) ? "dotnet" : hostPath;
    }
}
