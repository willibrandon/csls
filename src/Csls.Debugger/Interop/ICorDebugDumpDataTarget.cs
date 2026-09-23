using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

namespace Csls.Debugger.Interop;

/// <summary>
/// Exposes the public ICorDebugDataTarget callback ABI through generated COM interop.
/// </summary>
[GeneratedComInterface(Options = ComInterfaceOptions.ManagedObjectWrapper)]
[Guid("FE06DC28-49FB-4636-A4A3-E80DB4AE116C")]
internal unsafe partial interface ICorDebugDumpDataTarget
{
    /// <summary>
    /// Returns the captured target's CorDebugPlatform discriminator.
    /// </summary>
    [PreserveSig]
    int GetPlatform(int* platform);

    /// <summary>
    /// Copies captured target memory and reports successful partial reads.
    /// </summary>
    [PreserveSig]
    int ReadVirtual(ulong address, byte* buffer, uint requested, uint* read);

    /// <summary>
    /// Copies the captured native context of an operating-system thread.
    /// </summary>
    [PreserveSig]
    int GetThreadContext(uint threadId, uint flags, uint size, byte* context);
}
