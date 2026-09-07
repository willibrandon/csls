using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Csls.Debugger;

/// <summary>
/// Projects Linux native thread tracing with the platform's C long return type.
/// </summary>
[SupportedOSPlatform("linux")]
internal static partial class LinuxThreadContextNativeMethods
{
    /// <summary>
    /// Applies one native tracing operation from the thread that owns the trace relationship.
    /// </summary>
    /// <param name="request">The public ptrace request number.</param>
    /// <param name="threadId">The selected native thread identifier.</param>
    /// <param name="address">The operation's native address or register-set selector.</param>
    /// <param name="data">The operation's native data or signal argument.</param>
    /// <returns>The native result, with errno captured for a negative return.</returns>
    [LibraryImport("libc", EntryPoint = "ptrace", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    internal static partial CLong Trace(int request, int threadId, nint address, nint data);
}
