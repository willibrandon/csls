using Microsoft.Win32.SafeHandles;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.Json;

namespace Csls.Debugger.Tests;

/// <summary>
/// Retains bounded private memory around a stopped collector's registers without invoking another native dump writer.
/// </summary>
[SupportedOSPlatform("windows")]
internal static partial class WindowsNativeFaultMemoryCapture
{
    /// <summary>
    /// Records register-backed memory and one level of referenced storage while the caller owns the outstanding debug event.
    /// </summary>
    /// <param name="process">The caller-owned process suspended by Windows exception dispatch.</param>
    /// <param name="context">The actual native integer and control register context.</param>
    /// <param name="roots">The register values whose private storage is captured first.</param>
    /// <param name="testContext">Optionally associates the completed artifact with its individual test result.</param>
    /// <returns>The completed memory artifact path.</returns>
    internal static unsafe string Write(Process process, ReadOnlySpan<byte> context, ReadOnlySpan<nuint> roots,
        TestContext? testContext)
    {
        string directory = Path.Join(DebuggerTestEnvironment.FindRepositoryRoot(), "artifacts", "test-results");
        Directory.CreateDirectory(directory);
        string path = Path.Join(directory, $"native-fault-memory-{process.Id}-{Guid.NewGuid():N}.json");
        using (FileStream stream = File.Open(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteNumber("processId", process.Id);
            writer.WriteString("architecture", RuntimeInformation.ProcessArchitecture.ToString());
            writer.WriteBase64String("context", context);
            writer.WriteStartArray("regions");
            var captured = new Dictionary<nuint, byte[]>();
            var references = new List<nuint>();
            byte[] buffer = new byte[65536];
            foreach (nuint root in roots)
            {
                Capture(process, writer, root, captured, references, buffer);
            }
            // Inspect only pointers from the register-backed windows, with explicit bounds on reads and output.
            int count = Math.Min(references.Count, 256);
            for (int index = 0; index < count && captured.Count < 32; index++)
            {
                Capture(process, writer, references[index], captured, null, buffer);
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        testContext?.AddResultFile(path);
        return path;
    }

    private static unsafe void Capture(Process process, Utf8JsonWriter writer, nuint address, Dictionary<nuint, byte[]> captured,
        List<nuint>? references, byte[] buffer)
    {
        if (captured.Count >= 32 || address == 0)
        {
            return;
        }
        byte* entry = stackalloc byte[48];
        nuint windowStart = address & ~(nuint)65535;
        nuint queryAddress = windowStart;
        nuint regionStart;
        nuint regionEnd;
        while (true)
        {
            nuint queried = VirtualQueryEx(process.SafeHandle, queryAddress, entry, IntPtr.Size == 8 ? 48u : 28u);
            if (queried == 0)
            {
                int error = Marshal.GetLastPInvokeError();
                if (error == 87) // A register or stack word need not contain a valid user-space address.
                {
                    return;
                }
                throw new Win32Exception(error);
            }
            regionStart = Unsafe.ReadUnaligned<nuint>(entry);
            regionEnd = checked(regionStart + Unsafe.ReadUnaligned<nuint>(entry + 3 * sizeof(nint)));
            if (regionEnd <= queryAddress)
            {
                throw new IOException("Native fault memory returned a non-advancing address range.");
            }
            if (regionEnd > address)
            {
                break;
            }
            queryAddress = regionEnd;
        }
        uint state = Unsafe.ReadUnaligned<uint>(entry + 4 * sizeof(nint));
        uint protection = Unsafe.ReadUnaligned<uint>(entry + 4 * sizeof(nint) + sizeof(uint));
        uint type = Unsafe.ReadUnaligned<uint>(entry + 4 * sizeof(nint) + 2 * sizeof(uint));
        if (state != 0x1000 || type != 0x20000 || (protection & 0x101) != 0 || (protection & 0xee) == 0)
        {
            return;
        }
        // Query from the window boundary so different interior pointers share the same captured region.
        nuint start = Math.Max(regionStart, windowStart);
        if (captured.TryGetValue(start, out byte[]? retained))
        {
            CollectReferences(address, start, retained, references);
            return;
        }
        int capacity = checked((int)Math.Min((nuint)buffer.Length - (start - windowStart), regionEnd - start));
        int length = 0;
        int readError = 0;
        fixed (byte* bytes = buffer)
        {
            while (length < capacity)
            {
                nuint requested = checked((nuint)Math.Min(Environment.SystemPageSize, capacity - length));
                int success = ReadProcessMemory(process.SafeHandle, checked(start + (nuint)length), bytes + length,
                    requested, out nuint completed);
                readError = success == 0 ? Marshal.GetLastPInvokeError() : 0;
                if (completed > requested)
                {
                    throw new IOException("Native fault memory exceeded its requested buffer.");
                }
                length += checked((int)completed);
                if (success == 0 || completed != requested)
                {
                    break;
                }
            }
        }
        writer.WriteStartObject();
        writer.WriteNumber("address", (ulong)start);
        writer.WriteNumber("requested", capacity);
        writer.WriteNumber("readError", readError);
        writer.WriteBase64String("bytes", buffer.AsSpan(0, length));
        writer.WriteEndObject();
        byte[] bytesRead = buffer.AsSpan(0, length).ToArray();
        captured.Add(start, bytesRead);
        CollectReferences(address, start, bytesRead, references);
    }

    private static void CollectReferences(nuint address, nuint start, ReadOnlySpan<byte> bytes, List<nuint>? references)
    {
        if (references is null)
        {
            return;
        }
        int offset = checked((int)(address - start));
        offset -= offset % IntPtr.Size;
        if (offset >= bytes.Length)
        {
            return;
        }
        int added = 0;
        int available = bytes.Length - offset;
        foreach (nuint value in MemoryMarshal.Cast<byte, nuint>(bytes.Slice(offset, available - available % IntPtr.Size)))
        {
            if (added == 32 || references.Count == 256)
            {
                break;
            }
            if (value >= 65536 && value % (nuint)IntPtr.Size == 0 && !references.Contains(value))
            {
                references.Add(value);
                added++;
            }
        }
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
