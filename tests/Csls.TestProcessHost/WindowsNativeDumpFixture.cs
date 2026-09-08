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
    /// <param name="captureKind">The native threads, normal, triage, heap, or full capture policy.</param>
    /// <returns>Zero after the native writer successfully closes its output.</returns>
    internal static unsafe int Run(int processId, long creationTime, string path, string captureKind = "threads")
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(processId);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(creationTime);
        uint dumpType = captureKind switch
        {
            "threads" => 0x1000,
            "normal" => 0x1005,
            "triage" => 0x1205ac,
            "heap" => 0x41a25,
            "full" => 0x41827,
            _ => throw new ArgumentException("The native dump capture policy is invalid.", nameof(captureKind))
        };
        uint contextFlags = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X64 => 0x10001f,
            Architecture.Arm64 => 0x40001f,
            Architecture.X86 => 0x1003f,
            _ => throw new PlatformNotSupportedException("The collector requires a supported Windows architecture.")
        };
        // Query, read memory, duplicate handles, and clone the explicitly selected process.
        using SafeProcessHandle process = OpenProcess(access: 0x04d0, inheritHandle: 0, checked((uint)processId));
        if (process.IsInvalid || GetProcessTimes(process, out long actualCreationTime, out _, out _, out _) == 0)
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        }
        if (actualCreationTime != creationTime)
        {
            throw new InvalidOperationException("The selected process identity has changed.");
        }
        using SafeFileHandle file = File.OpenHandle(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
        // CoreCLR requests standard CONTEXT records; extended PSS records exceed its native buffer size.
        // The clone supplies its own address map; separately captured live image metadata can race module churn.
        uint status = PssCaptureSnapshot(process, captureFlags: 0x800001fd, contextFlags,
            out WindowsDumpSnapshotHandle captured);
        using WindowsDumpSnapshotHandle snapshot = captured;
        if (status != 0)
        {
            throw new Win32Exception(checked((int)status));
        }
        using SafeProcessHandle clone = snapshot.BorrowCloneProcess();
        using var memory = new WindowsDumpMappedMemory(process, clone, path);
        var state = GCHandle.Alloc(memory);
        try
        {
            nint* callback = stackalloc nint[2];
            callback[0] = (nint)(delegate* unmanaged[Stdcall]<nint, byte*, byte*, int>)&OnDumpCallback;
            callback[1] = GCHandle.ToIntPtr(state);
            int success = MiniDumpWriteDump(snapshot, checked((uint)processId), file, dumpType, 0, 0, (nint)callback);
            int error = Marshal.GetLastPInvokeError();
            memory.ThrowIfFailed();
            if (success == 0)
            {
                throw new IOException($"Native dump capture failed with HRESULT 0x{error:X8}.");
            }
        }
        finally
        {
            state.Free();
        }
        return 0;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static unsafe int OnDumpCallback(nint parameter, byte* input, byte* output)
    {
        if (input == null || output == null)
        {
            return 0;
        }
        return GCHandle.FromIntPtr(parameter).Target is WindowsDumpMappedMemory memory
            ? memory.OnCallback(input, output)
            : 0;
    }

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [LibraryImport("kernel32")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    private static partial uint PssCaptureSnapshot(SafeProcessHandle process, uint captureFlags, uint contextFlags,
        out WindowsDumpSnapshotHandle snapshot);

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
    private static partial int MiniDumpWriteDump(WindowsDumpSnapshotHandle process, uint processId, SafeFileHandle file,
        uint dumpType, nint exceptionInformation, nint userStreams, nint callback);
}
