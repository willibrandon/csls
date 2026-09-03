using System.Diagnostics;

namespace Csls.Debugger.Evaluation;

/// <summary>
/// Locates the evaluator bundled beside a supervised debugger worker.
/// </summary>
public static class DebuggerEvaluatorWorkerEnvironment
{
    /// <summary>
    /// Names the inherited absolute evaluator-worker path variable.
    /// </summary>
    public const string WorkerPathVariableName = "CSLS_DEBUGGER_EVALUATOR_WORKER_PATH";

    /// <summary>
    /// Adds the adjacent evaluator path to a debugger worker environment when available.
    /// </summary>
    /// <param name="startInfo">The debugger worker process start information.</param>
    /// <param name="debuggerWorkerPath">The absolute debugger worker executable or assembly.</param>
    public static void Configure(ProcessStartInfo startInfo, string debuggerWorkerPath)
    {
        ArgumentNullException.ThrowIfNull(startInfo);
        ArgumentException.ThrowIfNullOrWhiteSpace(debuggerWorkerPath);
        if (startInfo.Environment.TryGetValue(
            WorkerPathVariableName,
            out string? configured) &&
            !string.IsNullOrWhiteSpace(configured))
        {
            return;
        }

        string? evaluatorPath = FindBesideDebuggerWorker(debuggerWorkerPath);
        if (evaluatorPath is not null)
        {
            startInfo.Environment[WorkerPathVariableName] = evaluatorPath;
        }
    }

    /// <summary>
    /// Resolves the evaluator path inherited by the current debugger worker.
    /// </summary>
    /// <returns>The absolute evaluator executable or assembly path.</returns>
    public static string ResolveCurrentWorker()
    {
        string? configured = Environment.GetEnvironmentVariable(WorkerPathVariableName);
        if (!string.IsNullOrWhiteSpace(configured) && Path.IsPathFullyQualified(configured) &&
            File.Exists(configured))
        {
            return Path.GetFullPath(configured);
        }

        string? packaged = FindPackaged(AppContext.BaseDirectory);
        return packaged ?? throw new FileNotFoundException(
            "The managed debugger evaluator worker is missing. Reinstall csls.");
    }

    private static string? FindBesideDebuggerWorker(string debuggerWorkerPath)
    {
        string fullPath = Path.GetFullPath(debuggerWorkerPath);
        string directory = Path.GetDirectoryName(fullPath)
            ?? throw new InvalidOperationException(
                $"Debugger worker {fullPath} has no containing directory.");
        string? packaged = FindPackaged(directory);
        if (packaged is not null)
        {
            return packaged;
        }

        string configuration = Path.GetFileName(
            Path.TrimEndingDirectorySeparator(directory));
        string artifactsBin = Path.GetFullPath(Path.Join(directory, "..", ".."));
        string development = Path.Join(
            artifactsBin,
            "Csls.Debugger.Evaluator.Worker",
            configuration,
            "csls-debugger-evaluator-worker.dll");
        return File.Exists(development) ? development : null;
    }

    private static string? FindPackaged(string directory)
    {
        string executableName = OperatingSystem.IsWindows()
            ? "csls-debugger-evaluator-worker.exe"
            : "csls-debugger-evaluator-worker";
        string[] directories =
        [
            Path.Join(directory, "evaluator"),
            directory
        ];
        foreach (string candidateDirectory in directories)
        {
            string assembly = Path.Join(
                candidateDirectory,
                "csls-debugger-evaluator-worker.dll");
            if (!File.Exists(assembly))
            {
                continue;
            }

            string executable = Path.Join(candidateDirectory, executableName);
            return File.Exists(executable) ? executable : assembly;
        }

        return null;
    }
}
