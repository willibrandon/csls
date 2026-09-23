using Microsoft.Win32.SafeHandles;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace Csls.Debugger.Tests;

/// <summary>
/// Reads native fault identities, registers, and bounded memory while an independently owned collector is stopped.
/// </summary>
[SupportedOSPlatform("windows")]
internal static partial class WindowsNativeFaultReport
{
    /// <summary>
    /// Retains an exception record and its module-relative instruction address before native exception dispatch resumes.
    /// </summary>
    /// <param name="process">The retained process stopped at its current native event.</param>
    /// <param name="threadId">The operating-system thread reporting the exception.</param>
    /// <param name="record">The actual native exception record from Windows.</param>
    /// <param name="firstChance">Whether the runtime has yet to handle this exception.</param>
    /// <param name="diagnosticContext">Optionally retains native-image fault memory before exception dispatch resumes.</param>
    /// <param name="captureMemory">Whether this process still has its private-memory capture available.</param>
    /// <param name="memoryPath">The completed private-memory artifact, when one was captured.</param>
    /// <returns>One bounded record captured while the reporting thread is suspended.</returns>
    internal static unsafe string Read(Process process, uint threadId, byte* record, bool firstChance,
        TestContext? diagnosticContext, bool captureMemory, out string? memoryPath)
    {
        memoryPath = null;
        ulong address = Unsafe.ReadUnaligned<nuint>(record + 8 + sizeof(nint));
        var text = new StringBuilder();
        text.Append(CultureInfo.InvariantCulture,
            $"Native collector exception: process={process.Id} thread={threadId} first-chance={firstChance} code=0xC0000005 instruction=0x{address:X}.");
        if (Unsafe.ReadUnaligned<uint>(record + 8 + 2 * sizeof(nint)) >= 2)
        {
            int information = IntPtr.Size == 8 ? 32 : 20;
            nuint operation = Unsafe.ReadUnaligned<nuint>(record + information);
            nuint storage = Unsafe.ReadUnaligned<nuint>(record + information + sizeof(nint));
            text.Append(CultureInfo.InvariantCulture, $" operation={operation} address=0x{storage:X}.");
        }
        try
        {
            bool nativeImage = false;
            process.Refresh();
            foreach (ProcessModule module in process.Modules)
            {
                using (module)
                {
                    ulong start = checked((ulong)module.BaseAddress);
                    if (address >= start && address - start < checked((ulong)module.ModuleMemorySize))
                    {
                        nativeImage = true;
                        text.Append(CultureInfo.InvariantCulture,
                            $" module={module.ModuleName} offset=0x{address - start:X} base=0x{start:X} image-size=0x{module.ModuleMemorySize:X} version={module.FileVersionInfo.FileVersion}.");
                    }
                }
            }
            using SafeWaitHandle thread = OpenThread(8, 0, threadId); // THREAD_GET_CONTEXT
            if (thread.IsInvalid)
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError());
            }
            byte* storage = stackalloc byte[2048 + 15];
            byte* context = (byte*)(((nuint)storage + 15) & ~(nuint)15);
            new Span<byte>(context, 2048).Clear();
            (int offset, uint flags) = RuntimeInformation.ProcessArchitecture switch
            {
                Architecture.X64 => (48, 0x100003u),
                Architecture.Arm64 => (0, 0x400003u),
                Architecture.X86 => (0, 0x10003u),
                _ => throw new PlatformNotSupportedException("The native context requires a Windows process architecture.")
            };
            Unsafe.WriteUnaligned(context + offset, flags);
            if (GetThreadContext(thread, context) == 0)
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError());
            }
            text.Append(CultureInfo.InvariantCulture, $" context-prefix={Convert.ToHexString(new ReadOnlySpan<byte>(context, 272))}.");
            (string Name, int Offset)[] registers = RuntimeInformation.ProcessArchitecture switch
            {
                Architecture.X64 => [("stack", 152), ("register0", 128), ("register1", 136), ("register2", 184),
                    ("register3", 192), ("rbx", 144), ("rbp", 160), ("rsi", 168), ("rdi", 176)],
                Architecture.Arm64 => [("stack", 256), ("register0", 8), ("register1", 16), ("register2", 24),
                    ("register3", 32), ("x19", 160), ("x20", 168), ("x21", 176), ("x22", 184)],
                Architecture.X86 => [("stack", 196), ("register0", 176), ("register1", 172), ("register2", 168),
                    ("register3", 164), ("ebp", 180), ("esi", 160), ("edi", 156)],
                _ => throw new PlatformNotSupportedException("The native context requires a Windows process architecture.")
            };
            // Retain caller-owned state held in preserved registers as well as the native call arguments.
            foreach ((string name, int registerOffset) in registers)
            {
                AppendMemory(process, text, name, Unsafe.ReadUnaligned<nuint>(context + registerOffset), name == "stack" ? 2048 : 64);
            }
            if (captureMemory && (nativeImage || !firstChance))
            {
                Span<nuint> roots = stackalloc nuint[registers.Length];
                for (int index = 0; index < registers.Length; index++)
                {
                    roots[index] = Unsafe.ReadUnaligned<nuint>(context + registers[index].Offset);
                }
                memoryPath = WindowsNativeFaultMemoryCapture.Write(process, new ReadOnlySpan<byte>(context, 2048),
                    roots, diagnosticContext);
                text.Append(CultureInfo.InvariantCulture, $" memory-artifact={memoryPath}");
            }
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException or
            NotSupportedException or IOException or UnauthorizedAccessException or OverflowException)
        {
            text.Append(CultureInfo.InvariantCulture, $" Native detail unavailable: {exception.Message}");
        }
        return text.ToString();
    }

    private static unsafe void AppendMemory(Process process, StringBuilder text, string name, nuint address, int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(capacity, 2048);
        // Distinct small reads retain accessible stack bytes up to an unreadable page, without retrying a failed read.
        byte* bytes = stackalloc byte[2048];
        int length = 0;
        int error = 0;
        while (length < capacity)
        {
            nuint requested = checked((nuint)Math.Min(64, capacity - length));
            int success = ReadProcessMemory(process.SafeHandle, checked(address + (nuint)length), bytes + length,
                requested, out nuint completed);
            error = success == 0 ? Marshal.GetLastPInvokeError() : 0;
            if (completed > requested)
            {
                throw new IOException("Native fault memory returned more bytes than the requested buffer.");
            }
            length += checked((int)completed);
            if (success == 0 || completed != requested)
            {
                break;
            }
        }
        text.Append(CultureInfo.InvariantCulture,
            $" {name}-address=0x{address:X} {name}-bytes={Convert.ToHexString(new ReadOnlySpan<byte>(bytes, length))} {name}-error={error}.");
    }

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [LibraryImport("kernel32", SetLastError = true)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    private static unsafe partial int ReadProcessMemory(SafeProcessHandle process, nuint address, byte* buffer,
        nuint size, out nuint completed);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [LibraryImport("kernel32", SetLastError = true)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    private static partial SafeWaitHandle OpenThread(uint access, int inherit, uint threadId);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [LibraryImport("kernel32", SetLastError = true)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    private static unsafe partial int GetThreadContext(SafeWaitHandle thread, byte* context);
}
