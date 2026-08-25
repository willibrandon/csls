using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Csls.Control;

/// <summary>
/// Resolves the current POSIX account home without trusting mutable process environment variables.
/// </summary>
internal static partial class UnixUserProfileResolver
{
    private const int InitialBufferSize = 4 * 1024;
    private const int MaximumBufferSize = 1024 * 1024;
    private const int RangeError = 34;

    /// <summary>
    /// Gets the home directory recorded for the effective operating-system account.
    /// </summary>
    /// <returns>The absolute account home directory.</returns>
    internal static string GetCurrentUserHomeDirectory()
    {
        uint userId = GetEffectiveUserId();
        for (int bufferSize = InitialBufferSize;
            bufferSize <= MaximumBufferSize;
            bufferSize = checked(bufferSize * 2))
        {
            byte[] buffer = GC.AllocateUninitializedArray<byte>(bufferSize);
            var bufferHandle = GCHandle.Alloc(buffer, GCHandleType.Pinned);
            try
            {
                int error;
                nint result;
                nint homeDirectoryPointer;
                if (OperatingSystem.IsMacOS())
                {
                    error = GetMacOsPasswordEntry(
                        userId,
                        out MacOsPasswd account,
                        bufferHandle.AddrOfPinnedObject(),
                        (nuint)buffer.Length,
                        out result);
                    homeDirectoryPointer = account._directory;
                }
                else
                {
                    error = GetLinuxPasswordEntry(
                        userId,
                        out LinuxPasswd account,
                        bufferHandle.AddrOfPinnedObject(),
                        (nuint)buffer.Length,
                        out result);
                    homeDirectoryPointer = account._directory;
                }

                if (error == RangeError)
                {
                    continue;
                }

                if (error != 0)
                {
                    throw new Win32Exception(
                        error,
                        "The current Unix account could not be resolved.");
                }

                if (result == 0)
                {
                    throw new InvalidOperationException(
                        $"No Unix account exists for user ID {userId}.");
                }

                string homeDirectory = Marshal.PtrToStringUTF8(homeDirectoryPointer)
                    ?? throw new InvalidDataException(
                        "The current Unix account has no home directory.");
                if (!Path.IsPathFullyQualified(homeDirectory))
                {
                    throw new InvalidDataException(
                        $"The current Unix account home is not absolute: {homeDirectory}");
                }

                return homeDirectory;
            }
            finally
            {
                bufferHandle.Free();
            }
        }

        throw new InvalidDataException(
            $"The current Unix account record exceeded {MaximumBufferSize} bytes.");
    }

    [LibraryImport("libc", EntryPoint = "geteuid")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial uint GetEffectiveUserId();

    [LibraryImport("libc", EntryPoint = "getpwuid_r")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial int GetLinuxPasswordEntry(
        uint userId,
        out LinuxPasswd account,
        nint buffer,
        nuint bufferLength,
        out nint result);

    [LibraryImport("libc", EntryPoint = "getpwuid_r")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial int GetMacOsPasswordEntry(
        uint userId,
        out MacOsPasswd account,
        nint buffer,
        nuint bufferLength,
        out nint result);
}
