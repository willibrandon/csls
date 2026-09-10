using Microsoft.Win32.SafeHandles;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Csls.Debugger;

/// <summary>
/// Preserves Windows process identity and compares kernel creation times through query-only handles.
/// </summary>
[SupportedOSPlatform("windows")]
internal static partial class WindowsProcessIdentity
{
    /// <summary>
    /// Acquires an owned query handle that keeps the selected process identifier from being reused.
    /// </summary>
    /// <param name="processId">The positive operating-system process identifier.</param>
    /// <returns>The owned handle, or an invalid handle when the process has already retired.</returns>
    internal static SafeProcessHandle Open(int processId)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(processId);
        SafeProcessHandle handle = OpenProcess(access: 0x1000, inheritHandle: 0, checked((uint)processId));
        try
        {
            if (handle.IsInvalid)
            {
                int error = Marshal.GetLastPInvokeError();
                if (error != 87)
                {
                    throw new Win32Exception(error, $"Cannot query process {processId} for child ownership.");
                }
            }
            return handle;
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Verifies that a candidate was created after the exact retained parent process.
    /// </summary>
    /// <param name="candidate">The caller-owned query handle for a possible child.</param>
    /// <param name="parent">The caller-owned query handle for its possible parent.</param>
    /// <returns>Whether the candidate's kernel creation time follows the parent's creation time.</returns>
    internal static bool WasCreatedAfter(SafeProcessHandle candidate, SafeProcessHandle parent) =>
        GetCreationTime(candidate) > GetCreationTime(parent);

    /// <summary>
    /// Reads the creator identifier from the retained process instead of an earlier enumeration snapshot.
    /// </summary>
    /// <param name="candidate">The caller-owned query handle for the possible child.</param>
    /// <param name="parentProcessId">The positive identifier of the retained possible parent.</param>
    /// <returns>Whether the kernel process information records the selected creator identifier.</returns>
    internal static unsafe bool HasParent(SafeProcessHandle candidate, int parentProcessId)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(parentProcessId);
        int status = NtQueryInformationProcess(candidate, informationClass: 0,
            out WindowsProcessBasicInformation information, checked((uint)sizeof(WindowsProcessBasicInformation)),
            out uint returnedLength);
        if (status != 0)
        {
            throw new IOException($"Cannot query process parent information: NTSTATUS 0x{status:X8}.");
        }
        if (returnedLength != sizeof(WindowsProcessBasicInformation))
        {
            throw new InvalidDataException("Windows returned an incomplete process identity.");
        }
        return information._parentProcessId == checked((nuint)parentProcessId);
    }

    private static long GetCreationTime(SafeProcessHandle process)
    {
        if (GetProcessTimes(process, out long creation, out _, out _, out _) == 0)
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        }
        return creation;
    }

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [LibraryImport("kernel32", SetLastError = true)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    private static partial SafeProcessHandle OpenProcess(uint access, int inheritHandle, uint processId);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [LibraryImport("kernel32", SetLastError = true)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    private static partial int GetProcessTimes(SafeProcessHandle process, out long creation, out long exit,
        out long kernel, out long user);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [LibraryImport("ntdll")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    private static partial int NtQueryInformationProcess(SafeProcessHandle process, int informationClass,
        out WindowsProcessBasicInformation information, uint informationLength, out uint returnedLength);
}
