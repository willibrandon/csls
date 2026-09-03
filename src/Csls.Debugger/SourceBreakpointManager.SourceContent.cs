using Csls.Debugger.Contracts;
using System.Reflection.Metadata;

namespace Csls.Debugger;

/// <summary>
/// Catalogs checksummed source documents and session-local embedded content.
/// </summary>
internal sealed partial class SourceBreakpointManager
{
    private const int MaximumLocalSourceBytes = 32 * 1024 * 1024;
    private readonly Dictionary<string, DebugSourceRegistration> _sources =
        new(PathComparer);
    private readonly Dictionary<int, DebugSourceRegistration> _sourcesByReference = [];
    private readonly SourceLinkPolicy _sourceLinkPolicy = new();
    private readonly SourcePathMapper _sourcePathMapper = new();
    private int _nextSourceReference;

    /// <summary>
    /// Resolves one stack-frame document into its stable session source identity.
    /// </summary>
    /// <param name="modulePath">The module containing the Portable PDB identity.</param>
    /// <param name="sourcePath">The exact document path from the Portable PDB.</param>
    /// <returns>The stable source descriptor.</returns>
    internal DebugSourceInfo GetSourceInfo(string modulePath, string sourcePath)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        string key = CreateSourceKey(modulePath, sourcePath);
        if (_sources.TryGetValue(key, out DebugSourceRegistration? existing))
        {
            return existing.Info;
        }

        using var symbols = PortablePdbReader.TryOpen(modulePath);
        if (symbols is not null)
        {
            foreach (DocumentHandle handle in symbols.Metadata.Documents)
            {
                string candidate = symbols.Metadata.GetString(
                    symbols.Metadata.GetDocument(handle).Name);
                if (PathsEqual(candidate, sourcePath))
                {
                    return RegisterSource(modulePath, symbols, handle).Info;
                }
            }
        }

        return RegisterUnavailableSource(modulePath, sourcePath).Info;
    }

    /// <summary>
    /// Replaces build-time to local source mappings before runtime binding.
    /// </summary>
    /// <param name="mappings">The complete source path mapping dictionary.</param>
    /// <param name="sourceLinkOptions">The complete Source Link URL policy.</param>
    internal void SetSourceOptions(
        IReadOnlyDictionary<string, string> mappings,
        IReadOnlyDictionary<string, bool> sourceLinkOptions)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        _sourcePathMapper.Set(mappings);
        _sourceLinkPolicy.Set(sourceLinkOptions);
        ClearSources();
    }

    private DebugSourceRegistration RegisterSource(
        string modulePath,
        PortablePdbReader symbols,
        DocumentHandle handle)
    {
        PortablePdbSourceDocument document = PortablePdbSourceDocumentReader.Read(
            symbols.Metadata,
            handle,
            symbols.SourceLinkMappings);
        string key = CreateSourceKey(modulePath, document.Path);
        if (_sources.TryGetValue(key, out DebugSourceRegistration? existing))
        {
            return existing;
        }

        string resolvedPath = _sourcePathMapper.Map(document.Path);
        bool localSourceIsCurrent = document.EmbeddedSource is null &&
            IsLocalSourceCurrent(resolvedPath, document.Checksum);
        bool useSourceLink = document.EmbeddedSource is null &&
            !localSourceIsCurrent &&
            document.Checksum is not null &&
            document.SourceLinkUri is not null;
        int sourceReference = document.EmbeddedSource is null && !useSourceLink
            ? 0
            : checked(++_nextSourceReference);
        var registration = new DebugSourceRegistration
        {
            Info = new DebugSourceInfo(
                GetPortableFileName(document.Path),
                document.EmbeddedSource is not null || localSourceIsCurrent
                    ? resolvedPath
                    : null,
                sourceReference,
                document.EmbeddedSource is not null
                    ? "embedded source"
                    : localSourceIsCurrent
                        ? null
                        : useSourceLink
                            ? "Source Link"
                            : "original source is unavailable or does not match its checksum",
                document.Checksum),
            Content = document.EmbeddedSource is null
                ? null
                : new DebugSourceContent(
                    SourceTextDecoder.Decode(document.EmbeddedSource),
                    GetMimeType(GetPortableFileName(document.Path))),
            SourceLinkUri = useSourceLink ? document.SourceLinkUri : null
        };
        _sources.Add(key, registration);
        if (sourceReference > 0)
        {
            _sourcesByReference.Add(sourceReference, registration);
        }

        return registration;
    }

    private DebugSourceRegistration RegisterUnavailableSource(
        string modulePath,
        string sourcePath)
    {
        string key = CreateSourceKey(modulePath, sourcePath);
        var registration = new DebugSourceRegistration
        {
            Info = new DebugSourceInfo(
                GetPortableFileName(sourcePath),
                File.Exists(sourcePath) ? sourcePath : null,
                SourceReference: 0,
                Origin: File.Exists(sourcePath) ? null : "source is unavailable",
                Checksum: null)
        };
        _sources.Add(key, registration);
        return registration;
    }

    private static bool IsLocalSourceCurrent(
        string path,
        DebugSourceChecksum? checksum)
    {
        try
        {
            var file = new FileInfo(path);
            if (!file.Exists || file.Length > MaximumLocalSourceBytes)
            {
                return false;
            }

            if (checksum is null)
            {
                return true;
            }

            byte[] source = File.ReadAllBytes(path);
            return SourceChecksumVerifier.Matches(source, checksum);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static string GetMimeType(string path) =>
        Path.GetExtension(path).ToUpperInvariant() switch
        {
            ".CS" => "text/x-csharp",
            ".VB" => "text/x-vb",
            ".FS" or ".FSX" => "text/x-fsharp",
            _ => "text/plain"
        };

    private static string GetPortableFileName(string path) =>
        Path.GetFileName(path.Replace('\\', '/'));

    private static string CreateSourceKey(string modulePath, string sourcePath) =>
        $"{Path.GetFullPath(modulePath)}\0{sourcePath}";

    private void ClearSources()
    {
        _sources.Clear();
        _sourcesByReference.Clear();
    }
}
