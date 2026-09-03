using System.Runtime.InteropServices;

namespace Csls.Debugger;

/// <summary>
/// Exchanges exact Unix child status with the preloaded debugger wait interposer.
/// </summary>
internal static partial class UnixWaitStatusInterposer
{
    /// <summary>
    /// Initializes the preloaded native runtime before the debugger creates a child.
    /// </summary>
    internal static void Initialize() => InitializeCore();

    /// <summary>
    /// Selects the debugger-owned child whose exit status must be retained.
    /// </summary>
    /// <param name="processId">The positive direct-child process identifier.</param>
    internal static void Track(int processId)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(processId);
        TrackCore(processId);
    }

    /// <summary>
    /// Gets status captured when CoreCLR's transport poller reaped the child first.
    /// </summary>
    /// <param name="processId">The expected direct-child process identifier.</param>
    /// <param name="exitCode">Receives the decoded process or signal exit code.</param>
    /// <returns>True when the interposer retained exact status for this child.</returns>
    internal static bool TryGetExitCode(int processId, out int exitCode) =>
        TryGetExitCodeCore(processId, out exitCode) != 0;

    [LibraryImport("Csls.Debugger.UnixWait", EntryPoint = "csls_waitpid_track")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static partial void TrackCore(int processId);

    [LibraryImport("Csls.Debugger.UnixWait", EntryPoint = "csls_waitpid_initialize")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static partial void InitializeCore();

    [LibraryImport("Csls.Debugger.UnixWait", EntryPoint = "csls_waitpid_try_get_exit_code")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static partial int TryGetExitCodeCore(int processId, out int exitCode);
}
