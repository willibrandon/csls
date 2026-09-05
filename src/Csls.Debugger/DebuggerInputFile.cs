using Microsoft.Win32.SafeHandles;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Csls.Debugger;

/// <summary>
/// Opens debugger input files with owned handles and nonblocking Unix device semantics.
/// </summary>
internal static partial class DebuggerInputFile
{
    /// <summary>
    /// Opens a read-only stream without waiting for a Unix FIFO writer to connect.
    /// </summary>
    /// <param name="path">The absolute input-file path.</param>
    /// <returns>The stream that owns the native file handle.</returns>
    internal static FileStream OpenRead(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
        }

        int nonBlocking = OperatingSystem.IsMacOS() ? 0x0004 : 0x0800;
        int closeOnExec = OperatingSystem.IsMacOS() ? 0x01000000 : 0x00080000;
        int descriptor = Open(path, nonBlocking | closeOnExec);
        if (descriptor < 0)
        {
            int error = Marshal.GetLastPInvokeError();
            throw new IOException($"Cannot open envFile '{path}'.", new Win32Exception(error));
        }

        using var owner = new DisposableOwner<SafeFileHandle>();
        owner.Acquire(() => new SafeFileHandle(descriptor, ownsHandle: true));
        using var stream = new DisposableOwner<FileStream>();
        stream.Acquire(() => new FileStream(
            owner.Value ?? throw new InvalidOperationException("The input file has no handle."),
            FileAccess.Read, 4096, isAsync: false));
        _ = owner.Detach();
        return stream.Detach() ?? throw new InvalidOperationException("The input file has no stream.");
    }

    [LibraryImport("libc", EntryPoint = "open", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial int Open(string path, int flags);
}
