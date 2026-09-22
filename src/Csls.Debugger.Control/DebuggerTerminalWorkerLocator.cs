namespace Csls.Debugger.Control;

/// <summary>
/// Finds the installed debugger worker for a terminal reverse request.
/// </summary>
public static class DebuggerTerminalWorkerLocator
{
    /// <summary>
    /// Resolves the current worker executable or its managed entry assembly.
    /// </summary>
    /// <returns>The absolute existing worker path.</returns>
    public static string ResolveCurrent()
    {
        string? processPath = Environment.ProcessPath;
        if (!string.IsNullOrWhiteSpace(processPath) &&
            string.Equals(Path.GetFileNameWithoutExtension(processPath),
                "csls-debugger-worker", StringComparison.OrdinalIgnoreCase) &&
            File.Exists(processPath))
        {
            return Path.GetFullPath(processPath);
        }

        string assemblyPath = Path.Join(AppContext.BaseDirectory, "csls-debugger-worker.dll");
        if (File.Exists(assemblyPath))
        {
            return Path.GetFullPath(assemblyPath);
        }

        throw new FileNotFoundException("The installed debugger worker was not found.", assemblyPath);
    }
}
