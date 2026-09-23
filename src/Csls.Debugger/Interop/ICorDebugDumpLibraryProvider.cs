using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

namespace Csls.Debugger.Interop;

/// <summary>
/// Exposes the public ICLRDebuggingLibraryProvider3 callback ABI through generated COM interop.
/// </summary>
[GeneratedComInterface(Options = ComInterfaceOptions.ManagedObjectWrapper)]
[Guid("DE3AAB18-46A0-48B4-BF0D-2C336E69EA1B")]
internal unsafe partial interface ICorDebugDumpLibraryProvider
{
    /// <summary>
    /// Resolves a PE library identity and transfers an allocated UTF-16 path to dbgshim.
    /// </summary>
    [PreserveSig]
    int ProvideWindowsLibrary(char* name, char* runtimeModule, int indexType,
        uint timestamp, uint imageSize, nint* path);

    /// <summary>
    /// Resolves a Unix library identity and transfers an allocated UTF-16 path to dbgshim.
    /// </summary>
    [PreserveSig]
    int ProvideUnixLibrary(char* name, char* runtimeModule, int indexType,
        byte* buildId, int buildIdSize, nint* path);
}
