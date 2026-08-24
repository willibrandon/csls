namespace Csls.Client;

/// <summary>
/// Resolves the language-server worker bundled with a csls client tool.
/// </summary>
internal static class LanguageServerWorkerLocator
{
    private const string WorkerPathEnvironmentVariable = "CSLS_SERVER_WORKER_PATH";

    /// <summary>
    /// Returns the validated language-server worker path for a transient session.
    /// </summary>
    /// <returns>The absolute worker path.</returns>
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
                    $"The server worker configured by {WorkerPathEnvironmentVariable} does not exist.",
                    fullPath);
            }

            return fullPath;
        }

        string executableName = OperatingSystem.IsWindows()
            ? "csls-worker.exe"
            : "csls-worker";
        string siblingDirectory = Path.GetFullPath(
            Path.Join(AppContext.BaseDirectory, "..", "server"));
        string[] candidates =
        [
            Path.Join(siblingDirectory, executableName),
            Path.Join(siblingDirectory, "csls-worker.dll"),
            Path.Join(AppContext.BaseDirectory, "workers", "server", executableName),
            Path.Join(AppContext.BaseDirectory, "workers", "server", "csls-worker.dll")
        ];

        string? candidate = candidates.FirstOrDefault(File.Exists);
        if (candidate is not null)
        {
            return candidate;
        }

        throw new FileNotFoundException(
            "The csls language-server worker was not found beside the client worker. " +
            "Reinstall the csls tool.");
    }
}
