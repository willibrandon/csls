using Csls.Debugger;
using System.Diagnostics;

namespace Csls.App;

/// <summary>
/// Starts and supervises the interactive debugger worker with inherited terminal streams.
/// </summary>
internal static class DebuggerWorkerSupervisor
{
    /// <summary>
    /// Runs one normalized debugger request until it exits or the command is canceled.
    /// </summary>
    /// <param name="arguments">The normalized worker arguments.</param>
    /// <param name="cancellationToken">The launcher cancellation token.</param>
    /// <returns>The debugger worker exit code.</returns>
    internal static async Task<int> RunAsync(
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        string workerPath = DebuggerWorkerLocator.Resolve();
        bool managed = string.Equals(
            Path.GetExtension(workerPath),
            ".dll",
            StringComparison.OrdinalIgnoreCase);
        var startInfo = new ProcessStartInfo
        {
            FileName = managed ? ResolveDotNetHost() : workerPath,
            UseShellExecute = false
        };
        if (managed)
        {
            startInfo.ArgumentList.Add(workerPath);
        }

        foreach (string argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        DebuggerWorkerEnvironment.Configure(startInfo, workerPath);

        using Process process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("The csls debugger worker did not start.");
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

    private static string ResolveDotNetHost()
    {
        string? hostPath = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH");
        return string.IsNullOrWhiteSpace(hostPath) ? "dotnet" : hostPath;
    }
}
