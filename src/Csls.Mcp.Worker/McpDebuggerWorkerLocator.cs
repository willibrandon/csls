namespace Csls.Mcp.Worker;

/// <summary>
/// Resolves the debugger worker supervised by the MCP process.
/// </summary>
internal static class McpDebuggerWorkerLocator
{
    private const string WorkerPathEnvironmentVariable = "CSLS_DEBUGGER_WORKER_PATH";

    /// <summary>
    /// Resolves the configured or packaged debugger worker path.
    /// </summary>
    /// <returns>The absolute existing debugger worker path.</returns>
    internal static string Resolve()
    {
        return TryResolve() ?? throw new FileNotFoundException(
            "The csls debugger worker was not found beside the MCP worker. Reinstall csls-mcp.");
    }

    /// <summary>
    /// Resolves the debugger worker when this MCP installation includes one.
    /// </summary>
    /// <returns>The absolute worker path, or null for a language-only development host.</returns>
    internal static string? TryResolve()
    {
        string? configuredPath = Environment.GetEnvironmentVariable(
            WorkerPathEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            return Validate(configuredPath, WorkerPathEnvironmentVariable);
        }

        string executableName = OperatingSystem.IsWindows()
            ? "csls-debugger-worker.exe"
            : "csls-debugger-worker";
        string packageDirectory = Path.GetFullPath(
            Path.Join(AppContext.BaseDirectory, "..", "debugger"));
        string[] candidates =
        [
            Path.Join(packageDirectory, executableName),
            Path.Join(packageDirectory, "csls-debugger-worker.dll"),
            Path.Join(AppContext.BaseDirectory, executableName),
            Path.Join(AppContext.BaseDirectory, "csls-debugger-worker.dll")
        ];
        string? candidate = candidates.FirstOrDefault(File.Exists);
        if (candidate is not null)
        {
            return Path.GetFullPath(candidate);
        }

        return null;
    }

    private static string Validate(string path, string source)
    {
        string fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException(
                $"The debugger worker configured by {source} does not exist.",
                fullPath);
        }

        return fullPath;
    }
}
