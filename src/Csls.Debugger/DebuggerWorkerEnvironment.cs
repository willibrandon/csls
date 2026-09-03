using System.Diagnostics;

namespace Csls.Debugger;

/// <summary>
/// Configures the supervised debugger worker's platform lifecycle environment.
/// </summary>
public static class DebuggerWorkerEnvironment
{
    /// <summary>
    /// Initializes platform lifecycle support at the start of the debugger worker.
    /// </summary>
    public static void InitializeCurrentProcess()
    {
        if (!OperatingSystem.IsWindows())
        {
            UnixWaitStatusInterposer.Initialize();
        }
    }

    /// <summary>
    /// Adds the packaged Unix child-status interposer to a worker process start.
    /// </summary>
    /// <param name="startInfo">The worker process start information to configure.</param>
    /// <param name="workerPath">The absolute packaged worker executable or assembly path.</param>
    public static void Configure(ProcessStartInfo startInfo, string workerPath)
    {
        ArgumentNullException.ThrowIfNull(startInfo);
        ArgumentException.ThrowIfNullOrWhiteSpace(workerPath);
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        string libraryName = OperatingSystem.IsMacOS()
            ? "Csls.Debugger.UnixWait.dylib"
            : "Csls.Debugger.UnixWait.so";
        string workerDirectory = Path.GetDirectoryName(workerPath)
            ?? throw new InvalidOperationException(
                $"Debugger worker {workerPath} has no containing directory.");
        string libraryPath = Path.GetFullPath(Path.Join(
            workerDirectory,
            libraryName));
        if (!File.Exists(libraryPath))
        {
            throw new FileNotFoundException(
                "The debugger's Unix child-status interposer is missing. Reinstall csls.",
                libraryPath);
        }

        string variableName = OperatingSystem.IsMacOS()
            ? "DYLD_INSERT_LIBRARIES"
            : "LD_PRELOAD";
        startInfo.Environment.TryGetValue(variableName, out string? inherited);
        startInfo.Environment[variableName] = string.IsNullOrWhiteSpace(inherited)
            ? libraryPath
            : $"{libraryPath}{Path.PathSeparator}{inherited}";

        string searchVariableName = OperatingSystem.IsMacOS()
            ? "DYLD_LIBRARY_PATH"
            : "LD_LIBRARY_PATH";
        startInfo.Environment.TryGetValue(searchVariableName, out string? inheritedSearchPath);
        startInfo.Environment[searchVariableName] = string.IsNullOrWhiteSpace(inheritedSearchPath)
            ? workerDirectory
            : $"{workerDirectory}{Path.PathSeparator}{inheritedSearchPath}";
    }
}
