using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Csls.TestProcessHost;

/// <summary>
/// Reserves exception-dispatch stack space for deliberately overflowing Windows fixture threads.
/// </summary>
internal static partial class DebuggerStackGuarantee
{
    /// <summary>
    /// Reserves and verifies emergency stack space before the fixture exhausts its ordinary stack.
    /// </summary>
    internal static void ReserveForOverflow()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        // Windows saves the processor context and dispatches the exception on this same stack.
        // Keep an explicit 64 KiB emergency reserve for OS and CLR fatal-exception handling.
        const uint minimumBytes = 64 * 1024;
        uint bytes = minimumBytes;
        if (SetThreadStackGuarantee(ref bytes) == 0)
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        }

        bytes = 0;
        if (SetThreadStackGuarantee(ref bytes) == 0)
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        }
        if (bytes < minimumBytes)
        {
            throw new InvalidOperationException("Windows did not retain the fixture's exception stack reserve.");
        }
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    private static partial int SetThreadStackGuarantee(ref uint stackSizeInBytes);
}
