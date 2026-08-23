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
            Path.Combine(AppContext.BaseDirectory, "workers", "mcp", executableName),
            Path.Combine(AppContext.BaseDirectory, "workers", "mcp", "csls-mcp-worker.dll"),
            Path.Combine(AppContext.BaseDirectory, executableName),
            Path.Combine(AppContext.BaseDirectory, "csls-mcp-worker.dll")
        ];

        foreach (string candidate in candidates)
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new FileNotFoundException(
            "The csls MCP worker was not found beside the launcher. Reinstall csls-mcp.");
    }
}
