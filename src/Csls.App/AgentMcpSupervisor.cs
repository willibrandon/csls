using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;

namespace Csls.App;

/// <summary>
/// Locates and supervises the independently installed csls MCP launcher.
/// </summary>
internal static class AgentMcpSupervisor
{
    private const string LauncherPathEnvironmentVariable = "CSLS_MCP_LAUNCHER_PATH";

    /// <summary>
    /// Runs csls-mcp with inherited protocol streams until completion or cancellation.
    /// </summary>
    /// <param name="processId">The optional language-server process identifier.</param>
    /// <param name="socketPath">The optional explicit control socket path.</param>
    /// <param name="workspacePath">The optional transient workspace path.</param>
    /// <param name="cancellationToken">The launcher cancellation token.</param>
    /// <returns>The csls-mcp process exit code.</returns>
    internal static async Task<int> RunAsync(
        int? processId,
        string? socketPath,
        string? workspacePath,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<string> candidates;
        try
        {
            candidates = GetCandidates();
        }
        catch (FileNotFoundException exception)
        {
            await Console.Error.WriteLineAsync(exception.Message).ConfigureAwait(false);
            return 1;
        }

        foreach (string candidate in candidates)
        {
            try
            {
                using Process process = Process.Start(
                    CreateStartInfo(candidate, processId, socketPath, workspacePath))
                    ?? throw new InvalidOperationException(
                        "The csls MCP launcher did not start.");
                try
                {
                    await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    if (!process.HasExited)
                    {
                        process.Kill(entireProcessTree: true);
                        await process.WaitForExitAsync(CancellationToken.None)
                            .ConfigureAwait(false);
                    }

                    return 130;
                }

                return process.ExitCode;
            }
            catch (Win32Exception)
            {
                continue;
            }
        }

        await Console.Error.WriteLineAsync(
            "csls-mcp was not found. Install it with: dotnet tool install --global csls-mcp")
            .ConfigureAwait(false);
        return 1;
    }

    private static ProcessStartInfo CreateStartInfo(
        string launcherPath,
        int? processId,
        string? socketPath,
        string? workspacePath)
    {
        bool isManagedAssembly = string.Equals(
            Path.GetExtension(launcherPath),
            ".dll",
            StringComparison.OrdinalIgnoreCase);
        var startInfo = new ProcessStartInfo
        {
            FileName = isManagedAssembly ? ResolveDotNetHost() : launcherPath,
            UseShellExecute = false,
            WorkingDirectory = Environment.CurrentDirectory
        };
        if (isManagedAssembly)
        {
            startInfo.ArgumentList.Add(launcherPath);
        }

        if (processId.HasValue)
        {
            startInfo.ArgumentList.Add("--session");
            startInfo.ArgumentList.Add(processId.Value.ToString(CultureInfo.InvariantCulture));
        }
        else if (socketPath is not null)
        {
            startInfo.ArgumentList.Add("--socket");
            startInfo.ArgumentList.Add(socketPath);
        }
        else
        {
            startInfo.ArgumentList.Add("--workspace");
            startInfo.ArgumentList.Add(workspacePath!);
        }

        return startInfo;
    }

    private static IReadOnlyList<string> GetCandidates()
    {
        string? configuredPath = Environment.GetEnvironmentVariable(
            LauncherPathEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            string fullPath = Path.GetFullPath(configuredPath);
            if (!File.Exists(fullPath))
            {
                throw new FileNotFoundException(
                    $"The MCP launcher configured by {LauncherPathEnvironmentVariable} does not exist.",
                    fullPath);
            }

            return [fullPath];
        }

        string executableName = OperatingSystem.IsWindows() ? "csls-mcp.exe" : "csls-mcp";
        return
        [
            Path.Join(AppContext.BaseDirectory, executableName),
            executableName
        ];
    }

    private static string ResolveDotNetHost()
    {
        string? hostPath = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH");
        return string.IsNullOrWhiteSpace(hostPath) ? "dotnet" : hostPath;
    }
}
