using Microsoft.Win32.SafeHandles;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Csls.TestProcessHost;

/// <summary>
/// Retains an independently observed, terminated snapshot process until its owning test releases the input stream.
/// </summary>
[SupportedOSPlatform("windows")]
internal static partial class WindowsRetiredSnapshotFixture
{
    /// <summary>
    /// Captures and terminates an exact target's clone while retaining the retired process object for inspection.
    /// </summary>
    /// <param name="processId">The independently owned target process identifier.</param>
    /// <param name="creationTime">The target creation time in UTC file-time units.</param>
    /// <param name="announcement">The readiness text emitted once the snapshot process is terminated.</param>
    /// <returns>Zero after the test releases the retired snapshot handle.</returns>
    internal static int Run(int processId, long creationTime, string announcement)
    {
        using var target = Process.GetProcessById(processId);
        _ = target.SafeHandle;
        if (target.StartTime.ToUniversalTime().ToFileTimeUtc() != creationTime)
        {
            throw new InvalidOperationException("The selected process identity has changed.");
        }
        using SafeProcessHandle clone = CaptureClone(target.SafeHandle);
        if (TerminateProcess(clone, 0) == 0)
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        }
        uint wait = WaitForSingleObject(clone, 10000);
        if (wait == uint.MaxValue)
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        }
        if (wait != 0)
        {
            throw new TimeoutException("The retained snapshot process did not terminate.");
        }
        Console.WriteLine(announcement);
        Console.Out.Flush();
        // Keep the exited process object alive while the test retires the original target.
        _ = Console.ReadLine();
        return 0;
    }

    private static SafeProcessHandle CaptureClone(SafeProcessHandle target)
    {
        // Capture a virtual-address clone and release its executable section reference.
        uint status = PssCaptureSnapshot(target, 0x80000001, 0, out WindowsDumpSnapshotHandle snapshot);
        using (snapshot)
        {
            if (status != 0)
            {
                throw new Win32Exception(checked((int)status));
            }
            using SafeProcessHandle borrowed = snapshot.BorrowCloneProcess();
            // The borrowed handle lacks SYNCHRONIZE; request an independently owned waitable handle.
            int duplicated = DuplicateHandle(new nint(-1), borrowed, new nint(-1),
                out SafeProcessHandle retained, 0x101001, 0, 0);
            if (duplicated == 0)
            {
                int error = Marshal.GetLastPInvokeError();
                retained.Dispose();
                throw new Win32Exception(error);
            }
            return retained;
        }
    }

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [LibraryImport("kernel32")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    private static partial uint PssCaptureSnapshot(SafeProcessHandle process, uint flags, uint context,
        out WindowsDumpSnapshotHandle snapshot);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [LibraryImport("kernel32", SetLastError = true)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    private static partial int DuplicateHandle(nint source, SafeProcessHandle original, nint destination,
        out SafeProcessHandle duplicate, uint access, int inherit, uint options);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [LibraryImport("kernel32", SetLastError = true)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    private static partial uint WaitForSingleObject(SafeProcessHandle process, uint milliseconds);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [LibraryImport("kernel32", SetLastError = true)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    private static partial int TerminateProcess(SafeProcessHandle process, uint exitCode);
}
