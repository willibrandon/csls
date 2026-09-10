using Csls.Debugger.Interop;

namespace Csls.Debugger;

/// <summary>
/// Recovers the current Portable PDB snapshot retained by CoreCLR for a module.
/// </summary>
internal static class CorDebugInMemorySymbolReader
{
    private const int MaximumSymbolBytes = 256 * 1024 * 1024;

    /// <summary>
    /// Copies the current Portable PDB snapshot while the runtime remains stopped.
    /// </summary>
    /// <param name="module">The borrowed ICorDebugModule pointer.</param>
    /// <returns>The immutable Portable PDB image, or null when none is available.</returns>
    internal static unsafe byte[]? TryRead(nint module)
    {
        ArgumentOutOfRangeException.ThrowIfZero(module);
        if (!ComAbi.TryQueryInterface(
            module,
            ICorDebugModule3Abi.InterfaceId,
            out nint module3))
        {
            return null;
        }

        nint reader = 0;
        nint disposer = 0;
        try
        {
            Guid interfaceId = SymUnmanagedReader4Abi.InterfaceId;
            nint* readerAddress = &reader;
            int createResult = new ICorDebugModule3Abi(module3)
                .CreateReaderForInMemorySymbols((nint)(&interfaceId), (nint)readerAddress);
            reader = Volatile.Read(ref *readerAddress);
            if (createResult < 0 || reader == 0)
            {
                return null;
            }

            nint metadata = 0;
            int size = 0;
            nint* metadataAddress = &metadata;
            int* sizeAddress = &size;
            int metadataResult = new SymUnmanagedReader4Abi(reader)
                .GetPortableDebugMetadata((nint)metadataAddress, (nint)sizeAddress);
            metadata = Volatile.Read(ref *metadataAddress);
            size = Volatile.Read(ref *sizeAddress);
            if (metadataResult != 0 || metadata == 0 || size <= 0)
            {
                return null;
            }

            if (size > MaximumSymbolBytes)
            {
                throw new InvalidDataException(
                    $"The runtime Portable PDB snapshot exceeds the {MaximumSymbolBytes}-byte limit.");
            }

            return new ReadOnlySpan<byte>((void*)metadata, size).ToArray();
        }
        finally
        {
            if (reader != 0 && ComAbi.TryQueryInterface(
                reader,
                SymUnmanagedDisposeAbi.InterfaceId,
                out disposer))
            {
                _ = new SymUnmanagedDisposeAbi(disposer).Destroy();
            }

            if (disposer != 0)
            {
                _ = ComAbi.Release(disposer);
            }

            if (reader != 0)
            {
                _ = ComAbi.Release(reader);
            }

            _ = ComAbi.Release(module3);
        }
    }
}
