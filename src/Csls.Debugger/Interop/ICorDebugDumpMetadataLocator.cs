using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

namespace Csls.Debugger.Interop;

/// <summary>
/// Supplies identity-checked module metadata through the public ICorDebugMetaDataLocator callback ABI.
/// </summary>
[GeneratedComInterface(Options = ComInterfaceOptions.ManagedObjectWrapper)]
[Guid("7CEF8BA9-2EF7-42BF-973F-4171474F87D9")]
internal unsafe partial interface ICorDebugDumpMetadataLocator
{
    /// <summary>
    /// Copies the path of a retained metadata image into the caller's UTF-16 buffer, including its terminator.
    /// </summary>
    /// <param name="imagePath">The runtime's null-terminated recorded image path.</param>
    /// <param name="timestamp">The captured PE timestamp.</param>
    /// <param name="imageSize">The captured PE mapped-image size.</param>
    /// <param name="capacity">The destination capacity in UTF-16 characters.</param>
    /// <param name="length">Receives the required or written count, including the null terminator.</param>
    /// <param name="path">The caller-owned path buffer.</param>
    /// <returns>The HRESULT for success, insufficient buffer, or unavailable metadata.</returns>
    [PreserveSig]
    int GetMetaData(char* imagePath, uint timestamp, uint imageSize, uint capacity, uint* length, char* path);
}
