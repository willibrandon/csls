using Microsoft.Win32.SafeHandles;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Csls.TestProcessHost;

/// <summary>
/// Captures an explicitly selected Windows test process from an independently supervised collector.
/// </summary>
[SupportedOSPlatform("windows")]
internal static partial class WindowsNativeDumpFixture
{
    /// <summary>
    /// Writes native thread contexts and stacks after validating the retained process's creation time.
    /// </summary>
    /// <param name="processId">The positive identifier of the caller-owned test process.</param>
    /// <param name="creationTime">The expected UTC creation time in Windows file-time units.</param>
    /// <param name="path">The new dump file owned by the calling test.</param>
    /// <returns>Zero after the native writer successfully closes its output.</returns>
    internal static int Run(int processId, long creationTime, string path)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(processId);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(creationTime);
        using SafeProcessHandle process = OpenProcess(access: 0x0410, inheritHandle: 0, checked((uint)processId));
        if (process.IsInvalid || GetProcessTimes(process, out long actualCreationTime, out _, out _, out _) == 0)
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        }
        if (actualCreationTime != creationTime)
        {
            throw new InvalidOperationException("The selected process identity has changed.");
        }
        using SafeFileHandle file = File.OpenHandle(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        // Normal stacks plus native thread information; each collector makes exactly one DbgHelp call.
        if (MiniDumpWriteDump(process, checked((uint)processId), file, dumpType: 0x1000, 0, 0, 0) == 0)
        {
            throw new IOException($"Native dump capture failed with HRESULT 0x{Marshal.GetLastPInvokeError():X8}.");
        }
        return 0;
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
    [LibraryImport("dbghelp", SetLastError = true)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    private static partial int MiniDumpWriteDump(SafeProcessHandle process, uint processId, SafeFileHandle file,
        uint dumpType, nint exceptionInformation, nint userStreams, nint callback);
}
