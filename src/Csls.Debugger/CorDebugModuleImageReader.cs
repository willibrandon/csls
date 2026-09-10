using Csls.Debugger.Interop;
using System.Reflection.PortableExecutable;

namespace Csls.Debugger;

/// <summary>
/// Copies a bounded file-layout PE image retained by CoreCLR for an in-memory module.
/// </summary>
internal static class CorDebugModuleImageReader
{
    private const uint MaximumModuleImageBytes = 512 * 1024 * 1024;

    /// <summary>
    /// Reads and validates an in-memory module image while the runtime is stopped.
    /// </summary>
    /// <param name="module">The borrowed ICorDebugModule pointer.</param>
    /// <returns>The immutable PE image, or null when the runtime exposes no readable image.</returns>
    internal static unsafe byte[]? TryRead(nint module)
    {
        ArgumentOutOfRangeException.ThrowIfZero(module);
        var api = new ICorDebugModuleAbi(module);
        ulong address = 0;
        uint size = 0;
        ulong* addressPointer = &address;
        uint* sizePointer = &size;
        if (api.GetBaseAddress((nint)addressPointer) < 0 ||
            api.GetSize((nint)sizePointer) < 0)
        {
            return null;
        }

        address = Volatile.Read(ref *addressPointer);
        size = Volatile.Read(ref *sizePointer);
        if (address == 0 || size == 0 || size > MaximumModuleImageBytes)
        {
            return null;
        }

        nint process = 0;
        try
        {
            nint* processPointer = &process;
            if (api.GetProcess((nint)processPointer) < 0 ||
                (process = Volatile.Read(ref *processPointer)) == 0)
            {
                return null;
            }

            byte[] image = GC.AllocateUninitializedArray<byte>(checked((int)size));
            nuint bytesRead = 0;
            fixed (byte* imagePointer = image)
            {
                nuint* bytesReadPointer = &bytesRead;
                if (new ICorDebugProcessAbi(process).ReadMemory(
                    address,
                    size,
                    (nint)imagePointer,
                    (nint)bytesReadPointer) < 0)
                {
                    return null;
                }

                bytesRead = Volatile.Read(ref *bytesReadPointer);
            }

            if (bytesRead != size)
            {
                return null;
            }

            using var peReader = new PEReader(new MemoryStream(image, writable: false));
            return peReader.HasMetadata ? image : null;
        }
        catch (BadImageFormatException)
        {
            return null;
        }
        finally
        {
            if (process != 0)
            {
                _ = ComAbi.Release(process);
            }
        }
    }
}
