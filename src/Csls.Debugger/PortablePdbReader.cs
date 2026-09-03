using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

namespace Csls.Debugger;

/// <summary>
/// Owns validated Portable PDB metadata resolved from a managed module.
/// </summary>
internal sealed class PortablePdbReader : IDisposable
{
    private readonly MetadataReaderProvider _provider;

    private PortablePdbReader(
        MetadataReaderProvider provider,
        PortablePdbStorageKind storageKind,
        string? path)
    {
        _provider = provider;
        StorageKind = storageKind;
        Path = path;
        Metadata = provider.GetMetadataReader();
        SourceLinkMappings = PortablePdbSourceLinkResolver.Read(Metadata);
    }

    /// <summary>
    /// Gets the validated Portable PDB metadata.
    /// </summary>
    internal MetadataReader Metadata { get; }

    /// <summary>
    /// Gets the symbol storage kind.
    /// </summary>
    internal PortablePdbStorageKind StorageKind { get; }

    /// <summary>
    /// Gets the associated Portable PDB path, or null for embedded symbols.
    /// </summary>
    internal string? Path { get; }

    /// <summary>
    /// Gets the validated Source Link mappings ordered by specificity.
    /// </summary>
    internal IReadOnlyList<KeyValuePair<string, string>> SourceLinkMappings { get; }

    /// <summary>
    /// Opens matching associated or embedded Portable PDB metadata for one module.
    /// </summary>
    /// <param name="modulePath">The absolute managed PE path.</param>
    /// <param name="symbolPath">An explicit associated PDB candidate, or null for defaults.</param>
    /// <returns>An owned symbol reader, or null when matching symbols are unavailable.</returns>
    internal static PortablePdbReader? TryOpen(string modulePath, string? symbolPath = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modulePath);
        if (!System.IO.Path.IsPathFullyQualified(modulePath) || !File.Exists(modulePath))
        {
            return null;
        }

        using FileStream moduleStream = OpenRead(modulePath);
        using var peReader = new PEReader(moduleStream);
        MetadataReaderProvider? provider = null;
        try
        {
            if (peReader.TryOpenAssociatedPortablePdb(
                modulePath,
                symbolPath is null ? OpenIfPresent : _ => OpenIfPresent(symbolPath),
                out provider,
                out string? associatedPath) &&
                provider is not null)
            {
                var reader = new PortablePdbReader(
                    provider,
                    associatedPath is null
                        ? PortablePdbStorageKind.Embedded
                        : PortablePdbStorageKind.AssociatedFile,
                    associatedPath is null
                        ? null
                        : System.IO.Path.GetFullPath(symbolPath ?? associatedPath));
                provider = null;
                return reader;
            }

            return null;
        }
        finally
        {
            provider?.Dispose();
        }
    }

    /// <summary>
    /// Reads the first safe associated PDB identity from one managed PE image.
    /// </summary>
    /// <param name="modulePath">The absolute managed PE path.</param>
    /// <returns>The CodeView identity, or null when the image does not name a PDB.</returns>
    internal static PortablePdbReference? ReadReference(string modulePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modulePath);
        using FileStream moduleStream = OpenRead(modulePath);
        using var peReader = new PEReader(moduleStream);
        foreach (DebugDirectoryEntry entry in peReader.ReadDebugDirectory())
        {
            if (entry.Type != DebugDirectoryEntryType.CodeView)
            {
                continue;
            }

            CodeViewDebugDirectoryData data = peReader.ReadCodeViewDebugDirectoryData(entry);
            string fileName = GetPortableFileName(data.Path);
            if (fileName.Length != 0 && fileName.EndsWith(".pdb", StringComparison.OrdinalIgnoreCase))
            {
                return new PortablePdbReference(fileName, data.Path, data.Guid, data.Age);
            }
        }

        return null;
    }

    /// <summary>
    /// Releases the Portable PDB metadata owner.
    /// </summary>
    public void Dispose() => _provider.Dispose();

    private static FileStream OpenRead(string path) => new(
        path,
        FileMode.Open,
        FileAccess.Read,
        FileShare.Read | FileShare.Delete);

    private static FileStream? OpenIfPresent(string path)
    {
        try
        {
            return File.Exists(path) ? OpenRead(path) : null;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string GetPortableFileName(string path) =>
        System.IO.Path.GetFileName(path.Replace('\\', '/'));
}
