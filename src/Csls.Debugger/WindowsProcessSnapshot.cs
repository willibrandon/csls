using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Csls.Debugger;

/// <summary>
/// Enumerates parent relationships from one owned Windows process snapshot.
/// </summary>
[SupportedOSPlatform("windows")]
internal static partial class WindowsProcessSnapshot
{
    /// <summary>
    /// Lists direct children without retaining the operating system snapshot handle.
    /// </summary>
    internal static unsafe int[] GetChildren(int parentProcessId)
    {
        using WindowsProcessSnapshotHandle snapshot = CreateSnapshot(flags: 2, processId: 0);
        if (snapshot.IsInvalid)
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        }
        var children = new List<int>();
        WindowsProcessSnapshotEntry entry = default;
        entry._size = checked((uint)sizeof(WindowsProcessSnapshotEntry));
        int found = ReadFirst(snapshot, &entry);
        while (found != 0)
        {
            if (entry._parentProcessId == (uint)parentProcessId && entry._processId > 0)
            {
                children.Add(checked((int)entry._processId));
            }
            found = ReadNext(snapshot, &entry);
        }
        int error = Marshal.GetLastPInvokeError();
        if (error != 18)
        {
            throw new Win32Exception(error);
        }
        return [.. children.Order()];
    }

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [LibraryImport("kernel32", EntryPoint = "CreateToolhelp32Snapshot", SetLastError = true)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    private static partial WindowsProcessSnapshotHandle CreateSnapshot(uint flags, uint processId);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [LibraryImport("kernel32", EntryPoint = "Process32FirstW", SetLastError = true)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    private static unsafe partial int ReadFirst(WindowsProcessSnapshotHandle snapshot, WindowsProcessSnapshotEntry* entry);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [LibraryImport("kernel32", EntryPoint = "Process32NextW", SetLastError = true)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    private static unsafe partial int ReadNext(WindowsProcessSnapshotHandle snapshot, WindowsProcessSnapshotEntry* entry);
}
