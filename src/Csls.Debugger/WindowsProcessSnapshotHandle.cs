using Microsoft.Win32.SafeHandles;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Csls.Debugger;

/// <summary>
/// Releases one Windows Toolhelp snapshot through its matching kernel handle API.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed partial class WindowsProcessSnapshotHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    /// <summary>
    /// Creates the owned handle wrapper used by generated native marshalling.
    /// </summary>
    public WindowsProcessSnapshotHandle() : base(ownsHandle: true)
    {
    }

    /// <inheritdoc />
    protected override bool ReleaseHandle() => CloseHandle(handle) != 0;

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [LibraryImport("kernel32", SetLastError = true)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    private static partial int CloseHandle(nint value);
}
