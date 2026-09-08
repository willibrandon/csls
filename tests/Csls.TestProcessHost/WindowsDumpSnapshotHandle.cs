using Microsoft.Win32.SafeHandles;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Csls.TestProcessHost;

/// <summary>
/// Owns a Windows process snapshot and its cloned memory until the collector finishes writing.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed partial class WindowsDumpSnapshotHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    /// <summary>
    /// Creates the empty owner populated by the native snapshot API.
    /// </summary>
    public WindowsDumpSnapshotHandle() : base(ownsHandle: true)
    {
    }

    /// <summary>
    /// Borrows the clone process for memory queries while this snapshot remains owned.
    /// </summary>
    /// <returns>A non-owning wrapper that must be released before its snapshot.</returns>
    internal SafeProcessHandle BorrowCloneProcess()
    {
        uint status = PssQuerySnapshot(this, 1, out nint clone, checked((uint)IntPtr.Size));
        if (status != 0)
        {
            throw new Win32Exception(checked((int)status));
        }
        return new SafeProcessHandle(clone, ownsHandle: false);
    }

    /// <inheritdoc />
    protected override bool ReleaseHandle() => PssFreeSnapshot(new nint(-1), handle) == 0;

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [LibraryImport("kernel32")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    private static partial uint PssFreeSnapshot(nint ownerProcess, nint snapshot);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [LibraryImport("kernel32")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    private static partial uint PssQuerySnapshot(WindowsDumpSnapshotHandle snapshot, int informationClass,
        out nint cloneProcess, uint length);
}
