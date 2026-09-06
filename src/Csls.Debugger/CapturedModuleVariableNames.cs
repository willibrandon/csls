using Csls.Debugger.Contracts;
using System.Buffers.Binary;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

namespace Csls.Debugger;

/// <summary>
/// Resolves physical frame slots from captured module metadata and identity-matched symbols.
/// </summary>
public static class CapturedModuleVariableNames
{
    /// <summary>
    /// Reads source-authored argument or active-local names without loading the target assembly.
    /// </summary>
    /// <param name="moduleImage">A seekable captured image stream retained by the caller.</param>
    /// <param name="isLoadedImage">Whether the captured image uses virtual rather than file layout.</param>
    /// <param name="modulePath">The recorded path used only to locate adjacent symbols.</param>
    /// <param name="methodToken">The physical method definition token.</param>
    /// <param name="ilOffset">The captured instruction offset used to select active local scopes.</param>
    /// <param name="arguments">Whether to resolve parameters rather than locals.</param>
    /// <returns>Available source names keyed by physical runtime slot.</returns>
    public static IReadOnlyDictionary<int, string> Read(Stream moduleImage, bool isLoadedImage,
        string modulePath, uint methodToken, uint ilOffset, bool arguments)
    {
        ArgumentNullException.ThrowIfNull(moduleImage);
        ArgumentException.ThrowIfNullOrWhiteSpace(modulePath);
        using var peReader = new PEReader(moduleImage,
            PEStreamOptions.LeaveOpen | (isLoadedImage ? PEStreamOptions.IsLoadedImage : PEStreamOptions.Default));
        if (!peReader.HasMetadata || peReader.PEHeaders.MetadataSize > 64 * 1024 * 1024)
        {
            return new Dictionary<int, string>();
        }

        using DebugSymbolReader? symbols = TryReadSymbols(peReader, moduleImage, isLoadedImage, modulePath);
        IReadOnlyDictionary<int, ManagedSymbolVariable> variables;
        if (arguments)
        {
            DebugExpressionLanguage language = ReadLanguage(symbols, methodToken);
            using var metadata = new ManagedMetadataImage(peReader.GetMetadataReader(), []);
            variables = ManagedSymbolVariableNameResolver.GetArguments(metadata, methodToken, language);
        }
        else
        {
            variables = symbols?.GetLocalVariables(methodToken, ilOffset)
                ?? new Dictionary<int, ManagedSymbolVariable>();
        }

        return variables.ToDictionary(static pair => pair.Key, static pair => pair.Value.Name);
    }

    private static DebugExpressionLanguage ReadLanguage(DebugSymbolReader? symbols, uint methodToken)
    {
        try
        {
            return ManagedExpressionLanguageResolver.Resolve(symbols?.GetDocumentLanguage(methodToken) ?? Guid.Empty);
        }
        catch (Exception exception) when (DebugSymbolReader.IsReadFailure(exception))
        {
            return DebugExpressionLanguage.Common;
        }
    }

    private static DebugSymbolReader? TryReadSymbols(PEReader peReader, Stream image, bool isLoadedImage, string path)
    {
        const int maximumPdbBytes = 256 * 1024 * 1024;
        try
        {
            if (peReader.PEHeaders.PEHeader?.DebugTableDirectory.Size > 4096 * 28)
            {
                return null;
            }

            Span<byte> header = stackalloc byte[8];
            foreach (DebugDirectoryEntry entry in peReader.ReadDebugDirectory())
            {
                if (entry.DataSize > maximumPdbBytes)
                {
                    return null;
                }

                if (entry.Type != DebugDirectoryEntryType.EmbeddedPortablePdb)
                {
                    continue;
                }

                if (entry.DataSize < header.Length)
                {
                    return null;
                }

                image.Position = isLoadedImage ? entry.DataRelativeVirtualAddress : entry.DataPointer;
                image.ReadExactly(header);
                int size = BinaryPrimitives.ReadInt32LittleEndian(header[4..]);
                if (size is <= 0 or > maximumPdbBytes)
                {
                    return null;
                }
            }

            return DebugSymbolReader.TryOpen(peReader, path);
        }
        catch (Exception exception) when (DebugSymbolReader.IsReadFailure(exception))
        {
            return null;
        }
    }
}
