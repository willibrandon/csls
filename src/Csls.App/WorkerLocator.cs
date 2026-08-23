namespace Csls.App;

/// <summary>
/// Resolves the managed Roslyn worker shipped beside the Native AOT launcher.
/// </summary>
internal static class WorkerLocator
{
    private const string WorkerPathEnvironmentVariable = "CSLS_WORKER_PATH";

    /// <summary>
    /// Returns the validated worker path from configuration or the application directory.
    /// </summary>
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
                    $"The worker configured by {WorkerPathEnvironmentVariable} does not exist.",
                    fullPath);
            }

            return fullPath;
        }

        string executableName = OperatingSystem.IsWindows()
            ? "csls-worker.exe"
            : "csls-worker";
        string[] candidates =
        [
            Path.Join(AppContext.BaseDirectory, "workers", "server", executableName),
            Path.Join(AppContext.BaseDirectory, "workers", "server", "csls-worker.dll"),
            Path.Join(AppContext.BaseDirectory, executableName),
            Path.Join(AppContext.BaseDirectory, "csls-worker.dll")
        ];

        string? candidate = candidates.FirstOrDefault(File.Exists);
        if (candidate is not null)
        {
            return candidate;
        }

        throw new FileNotFoundException(
            "The csls Roslyn worker was not found beside the launcher. Reinstall csls.");
    }
}
