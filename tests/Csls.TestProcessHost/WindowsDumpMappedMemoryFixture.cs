using Microsoft.Win32.SafeHandles;
using System.ComponentModel;
using System.Diagnostics;
using System.IO.MemoryMappedFiles;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Csls.TestProcessHost;

/// <summary>
/// Compares captured shared-memory queries with Windows for pages inside one retained mapping.
/// </summary>
[SupportedOSPlatform("windows")]
internal static partial class WindowsDumpMappedMemoryFixture
{
    /// <summary>
    /// Queries first, interior, and final pages through a real process snapshot and backing file.
    /// </summary>
    /// <param name="path">The test-owned path for independently captured shared memory.</param>
    /// <returns>Zero after each query matches the Windows address-map contract.</returns>
    internal static unsafe int Run(string path)
    {
        int pageSize = Environment.SystemPageSize;
        using var mapping = MemoryMappedFile.CreateNew(null, 4L * pageSize);
        using MemoryMappedViewAccessor view = mapping.CreateViewAccessor();
        view.Write(0, 42);
        using var process = Process.GetCurrentProcess();
        uint status = PssCaptureSnapshot(process.SafeHandle, captureFlags: 1, contextFlags: 0,
            out WindowsDumpSnapshotHandle captured);
        using WindowsDumpSnapshotHandle snapshot = captured;
        if (status != 0)
        {
            throw new Win32Exception(checked((int)status));
        }
        using SafeProcessHandle clone = snapshot.BorrowCloneProcess();
        using var memory = new WindowsDumpMappedMemory(process.SafeHandle, clone, path);
        byte* mappingPointer = null;
        view.SafeMemoryMappedViewHandle.AcquirePointer(ref mappingPointer);
        try
        {
            ulong start = checked((ulong)(nuint)mappingPointer);
            byte* native = stackalloc byte[48];
            byte* result = stackalloc byte[52];
            foreach (int offset in new[] { 0, 1, pageSize, pageSize + 17, 4 * pageSize - 1 })
            {
                ulong address = checked(start + (uint)offset);
                if (VirtualQueryEx(process.SafeHandle, checked((nuint)address), native,
                    IntPtr.Size == 8 ? 48u : 28u) == 0)
                {
                    throw new Win32Exception(Marshal.GetLastPInvokeError());
                }
                int disposition = memory.QueryMemory(address, result);
                int queryStatus = Unsafe.ReadUnaligned<int>(result);
                if (disposition != 1 || queryStatus != 0)
                {
                    throw new IOException($"The captured query at mapping offset {offset} failed: {queryStatus:X8}.");
                }
                ulong nativeStart = Unsafe.ReadUnaligned<nuint>(native);
                ulong nativeSize = Unsafe.ReadUnaligned<nuint>(native + 3 * sizeof(nint));
                ulong capturedStart = Unsafe.ReadUnaligned<ulong>(result + sizeof(int));
                ulong capturedSize = Unsafe.ReadUnaligned<ulong>(result + sizeof(int) + 24);
                Console.WriteLine(FormattableString.Invariant(
                    $"{offset}:{nativeStart - start}:{nativeSize}:{capturedStart - start}:{capturedSize}"));
                if (Unsafe.ReadUnaligned<nuint>(native + sizeof(nint)) !=
                    Unsafe.ReadUnaligned<ulong>(result + sizeof(int) + 8))
                {
                    throw new IOException("The captured query changed the mapping's allocation base.");
                }
                for (int index = 0; index < 3; index++)
                {
                    if (Unsafe.ReadUnaligned<uint>(native + 4 * sizeof(nint) + index * sizeof(uint)) !=
                        Unsafe.ReadUnaligned<uint>(result + sizeof(int) + 32 + index * sizeof(uint)))
                    {
                        throw new IOException("The captured query changed the mapping's state, protection, or type.");
                    }
                }
            }
        }
        finally
        {
            view.SafeMemoryMappedViewHandle.ReleasePointer();
        }
        return 0;
    }

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [LibraryImport("kernel32")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    private static partial uint PssCaptureSnapshot(SafeProcessHandle process, uint captureFlags, uint contextFlags,
        out WindowsDumpSnapshotHandle snapshot);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [LibraryImport("kernel32", SetLastError = true)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    private static unsafe partial nuint VirtualQueryEx(SafeProcessHandle process, nuint address, byte* buffer, nuint length);
}
