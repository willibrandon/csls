using System.Diagnostics;
using System.Globalization;

namespace Csls.Mcp;

/// <summary>
/// Starts and supervises the managed MCP worker with inherited protocol streams.
/// </summary>
internal static class McpWorkerSupervisor
{
    /// <summary>
    /// Runs the managed MCP worker until it exits or the command is canceled.
    /// </summary>
    /// <param name="processId">The optional language-server process identifier.</param>
    /// <param name="socketPath">The optional explicit control socket path.</param>
    /// <param name="cancellationToken">The launcher cancellation token.</param>
    /// <returns>The MCP worker exit code.</returns>
    internal static async Task<int> RunAsync(
        int? processId,
        string? socketPath,
        CancellationToken cancellationToken)
    {
        string workerPath = McpWorkerLocator.Resolve();
        ProcessStartInfo startInfo = CreateStartInfo(workerPath, processId, socketPath);
        using Process process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("The csls MCP worker did not start.");

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
        int? processId,
        string? socketPath)
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

        if (processId.HasValue)
        {
            startInfo.ArgumentList.Add("--session");
            startInfo.ArgumentList.Add(processId.Value.ToString(CultureInfo.InvariantCulture));
        }
        else
        {
            startInfo.ArgumentList.Add("--socket");
            startInfo.ArgumentList.Add(Path.GetFullPath(socketPath!));
        }

        return startInfo;
    }

    private static string ResolveDotNetHost()
    {
        string? hostPath = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH");
        return string.IsNullOrWhiteSpace(hostPath) ? "dotnet" : hostPath;
    }
}
