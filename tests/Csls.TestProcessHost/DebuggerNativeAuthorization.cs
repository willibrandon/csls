using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Csls.TestProcessHost;

/// <summary>
/// Sets native inspection permissions on an independently launched test target.
/// </summary>
internal static partial class DebuggerNativeAuthorization
{
    /// <summary>
    /// Authorizes the selected debugger process and its descendants to inspect this fixture on Linux.
    /// </summary>
    /// <param name="debuggerProcessId">The exact debugger launcher process identifier.</param>
    internal static void AllowDebugger(int debuggerProcessId)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(debuggerProcessId);
        if (OperatingSystem.IsLinux())
        {
            const int setPtracer = 0x59616d61;
            if (Control(setPtracer, new CULong(checked((uint)debuggerProcessId)), default, default, default) != 0)
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError(), "The fixture could not authorize its debugger.");
            }
        }
    }

    /// <summary>
    /// Controls whether the current fixture permits unprivileged native inspection.
    /// </summary>
    /// <param name="enabled">Whether to enable this target's dumpability.</param>
    internal static void SetDumpable(bool enabled)
    {
        if (!OperatingSystem.IsLinux())
        {
            throw new PlatformNotSupportedException("Fixture dumpability control requires Linux.");
        }

        if (Control(4, new CULong(enabled ? 1U : 0U), default, default, default) != 0)
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "The fixture could not set native inspection permissions.");
        }
    }

    [LibraryImport("libc", EntryPoint = "prctl", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static partial int Control(int option, CULong value, CULong third, CULong fourth, CULong fifth);
}
