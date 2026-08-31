using Microsoft.Win32.SafeHandles;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Csls.Workspaces;

/// <summary>
/// Owns a Windows process tree and observes descendant termination through a job object.
/// </summary>
internal sealed partial class WindowsProcessTreeLifetime : IAsyncDisposable
{
    private const uint JobObjectBasicAccountingInformation = 1;
    private const uint JobObjectExtendedLimitInformation = 9;
    private const uint JobObjectLimitKillOnJobClose = 0x00002000;
    private readonly SafeFileHandle? _jobHandle;
    private int _disposeState;

    private WindowsProcessTreeLifetime(SafeFileHandle? jobHandle)
    {
        _jobHandle = jobHandle;
    }

    /// <summary>
    /// Assigns a process and its future descendants to one owned Windows job object.
    /// </summary>
    /// <param name="process">The process to own before it receives work.</param>
    /// <returns>The platform process-tree lifetime.</returns>
    internal static WindowsProcessTreeLifetime Attach(Process process)
    {
        ArgumentNullException.ThrowIfNull(process);
        if (!OperatingSystem.IsWindows())
        {
            return new WindowsProcessTreeLifetime(jobHandle: null);
        }

        using SafeFileHandle jobHandle = CreateJobObject(nint.Zero, nint.Zero);
        if (jobHandle.IsInvalid)
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        }

        var limits = new WindowsJobObjectExtendedLimitInformation
        {
            _basicLimitInformation = new WindowsJobObjectBasicLimitInformation
            {
                _limitFlags = JobObjectLimitKillOnJobClose
            }
        };
        if (SetInformationJobObject(
            jobHandle,
            JobObjectExtendedLimitInformation,
            in limits,
            checked((uint)Marshal.SizeOf<WindowsJobObjectExtendedLimitInformation>())) == 0)
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        }

        if (AssignProcessToJobObject(jobHandle, process.SafeHandle) == 0)
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        }

        var ownedJobHandle = new SafeFileHandle(
            jobHandle.DangerousGetHandle(),
            ownsHandle: true);
        jobHandle.SetHandleAsInvalid();
        return new WindowsProcessTreeLifetime(ownedJobHandle);
    }

    /// <summary>
    /// Terminates descendants left after the root exits and waits until every handle is released.
    /// </summary>
    /// <returns>A task that completes when the owned Windows job has no active processes.</returns>
    internal async ValueTask TerminateDescendantsAsync()
    {
        if (_jobHandle is null)
        {
            return;
        }

        uint activeProcesses = GetActiveProcessCount(_jobHandle);
        if (activeProcesses == 0)
        {
            return;
        }

        if (TerminateJobObject(_jobHandle, 0) == 0)
        {
            int error = Marshal.GetLastPInvokeError();
            if (GetActiveProcessCount(_jobHandle) != 0)
            {
                throw new Win32Exception(error);
            }

            return;
        }

        while (GetActiveProcessCount(_jobHandle) != 0)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(10)).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Terminates any remaining owned processes and releases the native job handle.
    /// </summary>
    /// <returns>A task that completes after deterministic process-tree cleanup.</returns>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposeState, 1) != 0)
        {
            return;
        }

        using SafeFileHandle? jobHandle = _jobHandle;
        await TerminateDescendantsAsync().ConfigureAwait(false);
    }

    private static uint GetActiveProcessCount(SafeFileHandle jobHandle)
    {
        if (QueryInformationJobObject(
            jobHandle,
            JobObjectBasicAccountingInformation,
            out WindowsJobObjectBasicAccountingInformation accounting,
            checked((uint)Marshal.SizeOf<WindowsJobObjectBasicAccountingInformation>()),
            nint.Zero) == 0)
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        }

        return accounting._activeProcesses;
    }

    [LibraryImport(
        "kernel32",
        EntryPoint = "CreateJobObjectW",
        SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    private static partial SafeFileHandle CreateJobObject(
        nint jobAttributes,
        nint name);

    [LibraryImport(
        "kernel32",
        EntryPoint = "SetInformationJobObject",
        SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    private static partial int SetInformationJobObject(
        SafeFileHandle jobHandle,
        uint informationClass,
        in WindowsJobObjectExtendedLimitInformation information,
        uint informationLength);

    [LibraryImport(
        "kernel32",
        EntryPoint = "AssignProcessToJobObject",
        SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    private static partial int AssignProcessToJobObject(
        SafeFileHandle jobHandle,
        SafeProcessHandle processHandle);

    [LibraryImport(
        "kernel32",
        EntryPoint = "QueryInformationJobObject",
        SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    private static partial int QueryInformationJobObject(
        SafeFileHandle jobHandle,
        uint informationClass,
        out WindowsJobObjectBasicAccountingInformation information,
        uint informationLength,
        nint returnLength);

    [LibraryImport(
        "kernel32",
        EntryPoint = "TerminateJobObject",
        SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    private static partial int TerminateJobObject(
        SafeFileHandle jobHandle,
        uint exitCode);
}
