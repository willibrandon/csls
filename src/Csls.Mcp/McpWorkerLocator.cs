namespace Csls.Mcp;

/// <summary>
/// Resolves the managed MCP worker shipped beside the Native AOT launcher.
/// </summary>
internal static class McpWorkerLocator
{
    private const string WorkerPathEnvironmentVariable = "CSLS_MCP_WORKER_PATH";

    /// <summary>
    /// Returns the validated worker path from configuration or the application directory.
    /// </summary>
    /// <returns>The absolute MCP worker path.</returns>
    internal static string Resolve()
    {
        string? configuredPath = Environment.GetEnvironmentVariable(
            WorkerPathEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            string fullPath = Path.GetFullPath(configuredPath);
            if (!File.Exists(fullPath))
            {
                throw new FileNotFoundException(
                    $"The MCP worker configured by {WorkerPathEnvironmentVariable} does not exist.",
                    fullPath);
            }

            return fullPath;
        }

        string executableName = OperatingSystem.IsWindows()
            ? "csls-mcp-worker.exe"
            : "csls-mcp-worker";
        string[] candidates =
        [
            Path.Join(AppContext.BaseDirectory, "workers", "mcp", executableName),
            Path.Join(AppContext.BaseDirectory, "workers", "mcp", "csls-mcp-worker.dll"),
            Path.Join(AppContext.BaseDirectory, executableName),
            Path.Join(AppContext.BaseDirectory, "csls-mcp-worker.dll")
        ];

        string? candidate = candidates.FirstOrDefault(File.Exists);
        if (candidate is not null)
        {
            return candidate;
        }

        throw new FileNotFoundException(
            "The csls MCP worker was not found beside the launcher. Reinstall csls-mcp.");
    }
}
