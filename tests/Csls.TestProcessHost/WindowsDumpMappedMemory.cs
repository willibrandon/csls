using Microsoft.Win32.SafeHandles;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Csls.TestProcessHost;

/// <summary>
/// Retains readable shared mappings alongside the private memory owned by a Windows process snapshot.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed partial class WindowsDumpMappedMemory : IDisposable
{
    private readonly FileStream _storage;
    private readonly SafeProcessHandle _clone;
    private readonly List<(ulong Address, ulong Size, long Position, byte[] Metadata)> _regions = [];
    private ExceptionDispatchInfo? _failure;

    /// <summary>
    /// Captures mapped pages into a temporary file using a bounded transfer buffer.
    /// </summary>
    /// <param name="process">The retained original process whose identity has been validated.</param>
    /// <param name="clone">The caller-owned clone handle retained until the native writer returns.</param>
    /// <param name="dumpPath">The new dump path used to name the independently owned temporary file.</param>
    internal WindowsDumpMappedMemory(SafeProcessHandle process, SafeProcessHandle clone, string dumpPath)
    {
        _clone = clone;
        _storage = new FileStream(dumpPath + ".mapped", FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None,
            4096, FileOptions.DeleteOnClose | FileOptions.RandomAccess);
        try
        {
            Capture(process);
            _regions.Sort(static (left, right) => left.Address.CompareTo(right.Address));
            _storage.Flush();
        }
        catch
        {
            _storage.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Supplies the clone's address map with separately captured shared mappings alongside native snapshot reads.
    /// </summary>
    /// <param name="input">The four-byte-packed native callback request.</param>
    /// <param name="output">The native callback response storage.</param>
    /// <returns>The native callback disposition for the requested operation.</returns>
    internal unsafe int OnCallback(byte* input, byte* output)
    {
        uint type = Unsafe.ReadUnaligned<uint>(input + sizeof(uint) + sizeof(nint));
        if (type is 16 or 17)
        {
            // S_FALSE selects the snapshot reader and enables virtual-memory callbacks.
            Unsafe.WriteUnaligned(output, 1);
        }
        else if (type == 20)
        {
            // Keep the native post-read result, including its completed byte count.
            return 0;
        }
        else if (type is 18 or 19)
        {
            try
            {
                byte* argument = input + 2 * sizeof(uint) + sizeof(nint);
                return type == 18 ? QueryMemory(argument, output) : ReadMappedMemory(argument, output);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or
                OverflowException or InvalidOperationException or Win32Exception)
            {
                // Retain I/O and range failures until the writer leaves the unmanaged callback boundary.
                _failure ??= ExceptionDispatchInfo.Capture(exception);
                Unsafe.WriteUnaligned(output, unchecked((int)0x80004005));
                Unsafe.WriteUnaligned(output + sizeof(int), 0u);
            }
        }
        return 1;
    }

    /// <summary>
    /// Rethrows a captured I/O error after the native writer returns to managed code.
    /// </summary>
    internal void ThrowIfFailed()
    {
        if (_failure is { } failure)
        {
            failure.Throw();
        }
    }

    /// <inheritdoc />
    public void Dispose() => _storage.Dispose();

    private unsafe void Capture(SafeProcessHandle process)
    {
        // MEMORY_BASIC_INFORMATION uses native alignment, unlike the minidump callback structures.
        byte* entry = stackalloc byte[48];
        byte[] buffer = new byte[1024 * 1024];
        for (ulong next = 0; ;)
        {
            if (VirtualQueryEx(process, checked((nuint)next), entry, NativeMemoryInfoSize) == 0)
            {
                int error = Marshal.GetLastPInvokeError();
                if (error == 87 && next != 0) // ERROR_INVALID_PARAMETER ends the user address space.
                {
                    break;
                }
                throw new Win32Exception(error);
            }
            ulong address = Unsafe.ReadUnaligned<nuint>(entry);
            ulong size = Unsafe.ReadUnaligned<nuint>(entry + 3 * sizeof(nint));
            if (size == 0 || address > ulong.MaxValue - size || address + size <= next)
            {
                throw new IOException("The captured mapping has an invalid address range.");
            }
            next = address + size;
            uint state = Unsafe.ReadUnaligned<uint>(entry + 4 * sizeof(nint));
            uint protection = Unsafe.ReadUnaligned<uint>(entry + 4 * sizeof(nint) + sizeof(uint));
            uint type = Unsafe.ReadUnaligned<uint>(entry + 4 * sizeof(nint) + 2 * sizeof(uint));
            // Only committed, readable, non-guard shared mappings need this independent memory source.
            if (type != 0x40000 || state != 0x1000 || (protection & 0x101) != 0 || (protection & 0xee) == 0)
            {
                continue;
            }
            byte[] metadata = new byte[48];
            fixed (byte* destination = metadata)
            {
                WriteMemoryInfo(entry, destination);
            }
            long position = _storage.Position;
            for (ulong offset = 0; offset < size;)
            {
                int length = checked((int)Math.Min((ulong)buffer.Length, size - offset));
                fixed (byte* bytes = buffer)
                {
                    int success = ReadProcessMemory(process, checked((nuint)(address + offset)), bytes,
                        checked((nuint)length), out nuint completed);
                    if (success == 0)
                    {
                        throw new Win32Exception(Marshal.GetLastPInvokeError());
                    }
                    if (completed != checked((nuint)length))
                    {
                        throw new IOException("The mapped-memory capture returned an incomplete region.");
                    }
                }
                _storage.Write(buffer, 0, length);
                offset += checked((uint)length);
            }
            _regions.Add((address, size, position, metadata));
        }
    }

    private unsafe int QueryMemory(byte* input, byte* output)
    {
        ulong address = Unsafe.ReadUnaligned<ulong>(input);
        int next = FindNextRegion(address);
        if (next < _regions.Count && _regions[next].Address <= address)
        {
            _regions[next].Metadata.CopyTo(new Span<byte>(output + sizeof(int), 48));
        }
        else
        {
            byte* entry = stackalloc byte[48];
            if (VirtualQueryEx(_clone, checked((nuint)address), entry, NativeMemoryInfoSize) == 0)
            {
                int error = Marshal.GetLastPInvokeError();
                if (error != 87)
                {
                    throw new Win32Exception(error);
                }
                // The minidump memory provider reports the end of its address space with E_NOINTERFACE.
                Unsafe.WriteUnaligned(output, unchecked((int)0x80004002));
                return 1;
            }
            ulong start = Unsafe.ReadUnaligned<nuint>(entry);
            ulong end = checked(start + Unsafe.ReadUnaligned<nuint>(entry + 3 * sizeof(nint)));
            if (next > 0)
            {
                (ulong previous, ulong size, _, _) = _regions[next - 1];
                start = Math.Max(start, checked(previous + size));
            }
            if (next < _regions.Count)
            {
                end = Math.Min(end, _regions[next].Address);
            }
            if (address < start || address >= end)
            {
                throw new IOException("The captured address-space regions overlap.");
            }
            WriteMemoryInfo(entry, output + sizeof(int));
            Unsafe.WriteUnaligned(output + sizeof(int), start);
            Unsafe.WriteUnaligned(output + sizeof(int) + 24, end - start);
        }
        Unsafe.WriteUnaligned(output, 0);
        return 1;
    }

    private unsafe int ReadMappedMemory(byte* input, byte* output)
    {
        ulong address = Unsafe.ReadUnaligned<ulong>(input);
        int index = FindRegion(address);
        if (index < 0)
        {
            return 0;
        }
        byte* buffer = (byte*)Unsafe.ReadUnaligned<nint>(input + sizeof(ulong));
        uint requested = Unsafe.ReadUnaligned<uint>(input + sizeof(ulong) + sizeof(nint));
        uint completed = 0;
        while (completed < requested && index >= 0)
        {
            (ulong start, ulong size, long position, _) = _regions[index];
            ulong current = checked(address + completed);
            int length = checked((int)Math.Min(int.MaxValue, Math.Min(requested - completed, size - (current - start))));
            long offset = checked(position + (long)(current - start));
            int read = RandomAccess.Read(_storage.SafeFileHandle, new Span<byte>(buffer + completed, length), offset);
            if (read == 0)
            {
                throw new EndOfStreamException("The captured mapping ended before the requested bytes.");
            }
            completed += checked((uint)read);
            index = FindRegion(checked(address + completed));
        }
        Unsafe.WriteUnaligned(output, 0);
        Unsafe.WriteUnaligned(output + sizeof(int), completed);
        return 1;
    }

    private int FindRegion(ulong address)
    {
        int index = FindNextRegion(address);
        return index < _regions.Count && _regions[index].Address <= address ? index : -1;
    }

    private int FindNextRegion(ulong address)
    {
        int low = 0;
        int high = _regions.Count;
        while (low < high)
        {
            int middle = low + (high - low) / 2;
            (ulong start, ulong size, _, _) = _regions[middle];
            if (start + size <= address)
            {
                low = middle + 1;
            }
            else
            {
                high = middle;
            }
        }
        return low;
    }

    private static nuint NativeMemoryInfoSize => IntPtr.Size == 8 ? 48u : 28u;

    private static unsafe void WriteMemoryInfo(byte* input, byte* output)
    {
        new Span<byte>(output, 48).Clear();
        Unsafe.WriteUnaligned(output, (ulong)Unsafe.ReadUnaligned<nuint>(input));
        Unsafe.WriteUnaligned(output + 8, (ulong)Unsafe.ReadUnaligned<nuint>(input + sizeof(nint)));
        Unsafe.WriteUnaligned(output + 16, Unsafe.ReadUnaligned<uint>(input + 2 * sizeof(nint)));
        Unsafe.WriteUnaligned(output + 24, (ulong)Unsafe.ReadUnaligned<nuint>(input + 3 * sizeof(nint)));
        Unsafe.WriteUnaligned(output + 32, Unsafe.ReadUnaligned<uint>(input + 4 * sizeof(nint)));
        Unsafe.WriteUnaligned(output + 36, Unsafe.ReadUnaligned<uint>(input + 4 * sizeof(nint) + sizeof(uint)));
        Unsafe.WriteUnaligned(output + 40, Unsafe.ReadUnaligned<uint>(input + 4 * sizeof(nint) + 2 * sizeof(uint)));
    }

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [LibraryImport("kernel32", SetLastError = true)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    private static unsafe partial nuint VirtualQueryEx(SafeProcessHandle process, nuint address, byte* buffer, nuint length);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [LibraryImport("kernel32", SetLastError = true)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    private static unsafe partial int ReadProcessMemory(SafeProcessHandle process, nuint address, byte* buffer,
        nuint size, out nuint completed);
}
