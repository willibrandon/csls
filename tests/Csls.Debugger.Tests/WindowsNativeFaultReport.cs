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
/// Reads native fault identities and integer registers while an independently owned collector is stopped.
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
    /// <returns>One bounded record captured while the reporting thread is suspended.</returns>
    internal static unsafe string Read(Process process, uint threadId, byte* record, bool firstChance)
    {
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
            process.Refresh();
            foreach (ProcessModule module in process.Modules)
            {
                using (module)
                {
                    ulong start = checked((ulong)module.BaseAddress);
                    if (address >= start && address - start < checked((ulong)module.ModuleMemorySize))
                    {
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
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException or
            NotSupportedException or IOException or UnauthorizedAccessException or OverflowException)
        {
            text.Append(CultureInfo.InvariantCulture, $" Native detail unavailable: {exception.Message}");
        }
        return text.ToString();
    }

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [LibraryImport("kernel32", SetLastError = true)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    private static partial SafeWaitHandle OpenThread(uint access, int inherit, uint threadId);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [LibraryImport("kernel32", SetLastError = true)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    private static unsafe partial int GetThreadContext(SafeWaitHandle thread, byte* context);
}
