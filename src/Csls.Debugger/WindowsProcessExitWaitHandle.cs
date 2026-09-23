using Microsoft.Win32.SafeHandles;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Csls.Debugger;

/// <summary>
/// Owns a noninheritable duplicate of a Windows process handle for kernel completion observation.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed partial class WindowsProcessExitWaitHandle : WaitHandle
{
    /// <summary>
    /// Retains the exact process object independently of its original managed owner.
    /// </summary>
    /// <param name="processHandle">The caller's retained process handle with synchronization access.</param>
    internal WindowsProcessExitWaitHandle(SafeProcessHandle processHandle)
    {
        const uint duplicateSameAccess = 2;
        nint currentProcess = -1;
        if (DuplicateHandle(currentProcess, processHandle, currentProcess, out SafeWaitHandle duplicate,
            desiredAccess: 0, inheritHandle: 0, duplicateSameAccess) == 0)
        {
            int error = Marshal.GetLastPInvokeError();
            duplicate.Dispose();
            throw new Win32Exception(error, "The debugger could not retain the process completion handle.");
        }

        SafeWaitHandle = duplicate;
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    private static partial int DuplicateHandle(nint sourceProcess, SafeProcessHandle sourceHandle,
        nint targetProcess, out SafeWaitHandle targetHandle, uint desiredAccess, int inheritHandle, uint options);
}
