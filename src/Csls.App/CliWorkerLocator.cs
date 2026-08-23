namespace Csls.App;

/// <summary>
/// Resolves the managed control worker shipped beside the Native AOT launcher.
/// </summary>
internal static class CliWorkerLocator
{
    private const string WorkerPathEnvironmentVariable = "CSLS_CLI_WORKER_PATH";

    /// <summary>
    /// Returns the validated CLI worker path from configuration or the application directory.
    /// </summary>
    /// <returns>The absolute CLI worker path.</returns>
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
                    $"The CLI worker configured by {WorkerPathEnvironmentVariable} does not exist.",
                    fullPath);
            }

            return fullPath;
        }

        string executableName = OperatingSystem.IsWindows()
            ? "csls-cli-worker.exe"
            : "csls-cli-worker";
        string[] candidates =
        [
            Path.Combine(AppContext.BaseDirectory, "workers", "cli", executableName),
            Path.Combine(AppContext.BaseDirectory, "workers", "cli", "csls-cli-worker.dll"),
            Path.Combine(AppContext.BaseDirectory, executableName),
            Path.Combine(AppContext.BaseDirectory, "csls-cli-worker.dll")
        ];

        foreach (string candidate in candidates)
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new FileNotFoundException(
            "The csls CLI worker was not found beside the launcher. Reinstall csls.");
    }
}
