namespace Csls.App;

/// <summary>
/// Resolves the managed interactive debugger worker shipped beside the launcher.
/// </summary>
internal static class DebuggerWorkerLocator
{
    private const string WorkerPathEnvironmentVariable = "CSLS_DEBUGGER_WORKER_PATH";

    /// <summary>
    /// Returns the validated debugger worker path from configuration or the application directory.
    /// </summary>
    /// <returns>The absolute debugger worker path.</returns>
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
                    $"The debugger worker configured by {WorkerPathEnvironmentVariable} does not exist.",
                    fullPath);
            }

            return fullPath;
        }

        string executableName = OperatingSystem.IsWindows()
            ? "csls-debugger-worker.exe"
            : "csls-debugger-worker";
        string[] candidates =
        [
            Path.Join(AppContext.BaseDirectory, "workers", "debugger", executableName),
            Path.Join(AppContext.BaseDirectory, "workers", "debugger", "csls-debugger-worker.dll"),
            Path.Join(AppContext.BaseDirectory, executableName),
            Path.Join(AppContext.BaseDirectory, "csls-debugger-worker.dll")
        ];

        string? candidate = candidates.FirstOrDefault(File.Exists);
        return candidate ?? throw new FileNotFoundException(
            "The csls debugger worker was not found beside the launcher. Reinstall csls.");
    }
}
