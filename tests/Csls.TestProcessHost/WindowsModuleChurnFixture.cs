using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Csls.TestProcessHost;

/// <summary>
/// Loads and unloads a real native image while retaining known private and mapped memory for dump inspection.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class WindowsModuleChurnFixture
{
    /// <summary>
    /// Announces allocation addresses and keeps changing the module list until its owner writes the stop file.
    /// </summary>
    /// <param name="stopPath">The test-owned file that ends native module churn.</param>
    /// <returns>Zero after releasing all allocations and the last loaded library.</returns>
    internal static unsafe int Run(string stopPath)
    {
        const int AllocationSize = 1024 * 1024;
        using var mapping = MemoryMappedFile.CreateNew(null, AllocationSize);
        using MemoryMappedViewAccessor view = mapping.CreateViewAccessor();
        byte* mapped = null;
        view.SafeMemoryMappedViewHandle.AcquirePointer(ref mapped);
        try
        {
            return RunWithReservedMapping(stopPath, mapped + view.PointerOffset);
        }
        finally
        {
            view.SafeMemoryMappedViewHandle.ReleasePointer();
        }
    }

    private static unsafe int RunWithReservedMapping(string stopPath, byte* mapped)
    {
        const int AllocationSize = 1024 * 1024;
        using var mapping = MemoryMappedFile.CreateNew(null, AllocationSize, MemoryMappedFileAccess.ReadWriteExecute,
            MemoryMappedFileOptions.DelayAllocatePages, HandleInheritability.None);
        using MemoryMappedViewAccessor view = mapping.CreateViewAccessor();
        byte* reserved = null;
        view.SafeMemoryMappedViewHandle.AcquirePointer(ref reserved);
        try
        {
            return RunChurn(stopPath, mapped, reserved + view.PointerOffset);
        }
        finally
        {
            view.SafeMemoryMappedViewHandle.ReleasePointer();
        }
    }

    private static unsafe int RunChurn(string stopPath, byte* mapped, byte* reserved)
    {
        const int AllocationSize = 1024 * 1024;
        void* allocation = NativeMemory.Alloc(AllocationSize);
        try
        {
            new Span<byte>(allocation, AllocationSize).Fill(0x5a);
            new Span<byte>(mapped, AllocationSize).Fill(0xa6);
            new Span<byte>(reserved, AllocationSize).Fill(0xc3);
            string library = Path.Join(Environment.SystemDirectory, "dbghelp.dll");
            long count = 0;
            do
            {
                nint loaded = NativeLibrary.Load(library);
                NativeLibrary.Free(loaded);
                if (++count == 1)
                {
                    Console.WriteLine($"ready:{(nuint)allocation:x}:{(nuint)mapped:x}:{(nuint)reserved:x}");
                }
            }
            while (!File.Exists(stopPath));
            Console.WriteLine($"completed:{count}");
            return 0;
        }
        finally
        {
            NativeMemory.Free(allocation);
        }
    }
}
