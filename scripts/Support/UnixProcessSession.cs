using System.Runtime.InteropServices;

namespace Csls.Support;

/// <summary>
/// Isolates a Unix watchdog from signals sent to its parent process group.
/// </summary>
internal static partial class UnixProcessSession
{
    /// <summary>
    /// Creates a new session for the current Unix process and returns its native error code.
    /// </summary>
    /// <returns>Zero after success or on Windows, otherwise the native Unix error code.</returns>
    internal static int DetachCurrentProcess()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
        {
            return 0;
        }

        return SetSessionId() >= 0 ? 0 : Marshal.GetLastPInvokeError();
    }

    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    [LibraryImport("libc", EntryPoint = "setsid", SetLastError = true)]
    private static partial int SetSessionId();
}
