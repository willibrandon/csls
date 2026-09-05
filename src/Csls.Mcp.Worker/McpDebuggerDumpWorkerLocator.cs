namespace Csls.Mcp.Worker;

/// <summary>
/// Resolves the managed process-dump worker supervised by the MCP process.
/// </summary>
internal static class McpDebuggerDumpWorkerLocator
{
    private const string WorkerPathEnvironmentVariable =
        "CSLS_DEBUGGER_DUMP_WORKER_PATH";

    /// <summary>
    /// Resolves the dump worker when this MCP installation includes one.
    /// </summary>
    /// <returns>The absolute worker path, or null when dump inspection is unavailable.</returns>
    internal static string? TryResolve()
    {
        string? configuredPath = Environment.GetEnvironmentVariable(
            WorkerPathEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            return Validate(configuredPath);
        }

        string executableName = OperatingSystem.IsWindows()
            ? "csls-debugger-dump-worker.exe"
            : "csls-debugger-dump-worker";
        string packageDirectory = Path.GetFullPath(
            Path.Join(AppContext.BaseDirectory, "..", "debugger-dump"));
        string[] candidates =
        [
            Path.Join(packageDirectory, executableName),
            Path.Join(packageDirectory, "csls-debugger-dump-worker.dll"),
            Path.Join(AppContext.BaseDirectory, executableName),
            Path.Join(AppContext.BaseDirectory, "csls-debugger-dump-worker.dll")
        ];
        string? candidate = candidates.FirstOrDefault(File.Exists);
        return candidate is null ? null : Path.GetFullPath(candidate);
    }

    private static string Validate(string path)
    {
        string fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException(
                $"The dump worker configured by {WorkerPathEnvironmentVariable} does not exist.",
                fullPath);
        }

        return fullPath;
    }
}
