using Microsoft.Win32.SafeHandles;
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
    internal static int[] GetChildren(int parentProcessId)
    {
        var children = new List<int>();
        VisitChildren(parentProcessId, children.Add);
        return [.. children.Order()];
    }

    /// <summary>
    /// Visits creation-time-validated children while retaining their exact kernel process identities.
    /// </summary>
    /// <param name="parentProcessId">The positive identifier of the selected parent process.</param>
    /// <param name="visit">The operation performed while parent and child query handles remain owned.</param>
    internal static unsafe void VisitChildren(int parentProcessId, Action<int> visit)
    {
        ArgumentNullException.ThrowIfNull(visit);
        using SafeProcessHandle parent = WindowsProcessIdentity.Open(parentProcessId);
        if (parent.IsInvalid)
        {
            return;
        }
        using WindowsProcessSnapshotHandle snapshot = CreateSnapshot(flags: 2, processId: 0);
        if (snapshot.IsInvalid)
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        }
        WindowsProcessSnapshotEntry entry = default;
        entry._size = checked((uint)sizeof(WindowsProcessSnapshotEntry));
        int found = ReadFirst(snapshot, &entry);
        while (found != 0)
        {
            if (entry._parentProcessId == (uint)parentProcessId && entry._processId > 0)
            {
                int processId = checked((int)entry._processId);
                using SafeProcessHandle child = WindowsProcessIdentity.Open(processId);
                // Windows retains a creator PID after its process exits. A reused PID alone is not ownership.
                if (!child.IsInvalid && WindowsProcessIdentity.WasCreatedAfter(child, parent) &&
                    WindowsProcessIdentity.HasParent(child, parentProcessId))
                {
                    visit(processId);
                }
            }
            found = ReadNext(snapshot, &entry);
        }
        int error = Marshal.GetLastPInvokeError();
        if (error != 18)
        {
            throw new Win32Exception(error);
        }
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
