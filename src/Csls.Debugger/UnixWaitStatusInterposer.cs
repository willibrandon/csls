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

    /// <summary>
    /// Reserves a native thread's stop notifications before beginning register inspection.
    /// </summary>
    /// <param name="threadId">The thread whose native poll view must remain non-reaping.</param>
    internal static void BeginThreadInspection(int threadId)
    {
        int result = BeginThreadInspectionCore(threadId);
        if (result != 0)
        {
            throw new InvalidOperationException($"Native thread inspection could not reserve thread {threadId}: error {result}.");
        }
    }

    /// <summary>
    /// Releases the native poll exclusion after inspection has detached from its thread.
    /// </summary>
    /// <param name="threadId">The thread owned by the completed inspection.</param>
    internal static void EndThreadInspection(int threadId)
    {
        int result = EndThreadInspectionCore(threadId);
        if (result != 0)
        {
            throw new InvalidOperationException($"Native thread inspection lost ownership of thread {threadId}: error {result}.");
        }
    }

    /// <summary>
    /// Waits for a native child change from the debugger's sole reaping owner.
    /// </summary>
    /// <param name="processId">The selected child or traced thread.</param>
    /// <param name="status">Receives the exact native wait status.</param>
    /// <param name="options">The native wait options.</param>
    /// <returns>The native wait result with errno captured on failure.</returns>
    [LibraryImport("Csls.Debugger.UnixWait", EntryPoint = "csls_waitpid_wait", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    internal static partial int WaitProcess(int processId, out int status, int options);

    [LibraryImport("Csls.Debugger.UnixWait", EntryPoint = "csls_waitpid_begin_inspection")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static partial int BeginThreadInspectionCore(int threadId);

    [LibraryImport("Csls.Debugger.UnixWait", EntryPoint = "csls_waitpid_end_inspection")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static partial int EndThreadInspectionCore(int threadId);

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
