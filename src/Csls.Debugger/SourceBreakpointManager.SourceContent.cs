using Csls.Debugger.Contracts;
using System.Reflection.Metadata;
using System.Security.Cryptography;
using System.Text;

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
                    return RegisterSource(modulePath, symbols.Metadata, handle).Info;
                }
            }
        }

        return RegisterUnavailableSource(modulePath, sourcePath).Info;
    }

    /// <summary>
    /// Gets embedded source content by its positive session-local reference.
    /// </summary>
    /// <param name="sourceReference">The reference returned in a source descriptor.</param>
    /// <returns>The complete source text and media type.</returns>
    internal DebugSourceContent GetSourceContent(int sourceReference)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sourceReference);
        if (!_sourcesByReference.TryGetValue(
            sourceReference,
            out DebugSourceRegistration? registration) ||
            registration.Content is null)
        {
            throw new KeyNotFoundException(
                $"Source reference {sourceReference} does not exist in this debugger session.");
        }

        return registration.Content;
    }

    private DebugSourceRegistration RegisterSource(
        string modulePath,
        MetadataReader reader,
        DocumentHandle handle)
    {
        PortablePdbSourceDocument document = PortablePdbSourceDocumentReader.Read(reader, handle);
        string key = CreateSourceKey(modulePath, document.Path);
        if (_sources.TryGetValue(key, out DebugSourceRegistration? existing))
        {
            return existing;
        }

        int sourceReference = document.EmbeddedSource is null
            ? 0
            : checked(++_nextSourceReference);
        bool localSourceIsCurrent = document.EmbeddedSource is null &&
            IsLocalSourceCurrent(document.Path, document.Checksum);
        var registration = new DebugSourceRegistration
        {
            Info = new DebugSourceInfo(
                Path.GetFileName(document.Path),
                sourceReference > 0 || localSourceIsCurrent ? document.Path : null,
                sourceReference,
                sourceReference > 0
                    ? "embedded source"
                    : localSourceIsCurrent
                        ? null
                        : "original source is unavailable or does not match its checksum",
                document.Checksum),
            Content = document.EmbeddedSource is null
                ? null
                : new DebugSourceContent(
                    DecodeSource(document.EmbeddedSource),
                    GetMimeType(document.Path))
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
                Path.GetFileName(sourcePath),
                File.Exists(sourcePath) ? sourcePath : null,
                SourceReference: 0,
                Origin: File.Exists(sourcePath) ? null : "source is unavailable",
                Checksum: null)
        };
        _sources.Add(key, registration);
        return registration;
    }

    private static string DecodeSource(byte[] source)
    {
        using var stream = new MemoryStream(source, writable: false);
        using var reader = new StreamReader(
            stream,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true),
            detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
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

            if (checksum.Algorithm != "SHA256")
            {
                return false;
            }

            using FileStream stream = File.Open(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read | FileShare.Delete);
            return string.Equals(
                Convert.ToHexString(SHA256.HashData(stream)),
                checksum.Value,
                StringComparison.OrdinalIgnoreCase);
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

    private static string CreateSourceKey(string modulePath, string sourcePath) =>
        $"{Path.GetFullPath(modulePath)}\0{sourcePath}";

    private void ClearSources()
    {
        _sources.Clear();
        _sourcesByReference.Clear();
    }
}
