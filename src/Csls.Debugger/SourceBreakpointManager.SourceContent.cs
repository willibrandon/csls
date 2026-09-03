using Csls.Debugger.Contracts;

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
    /// <param name="modulePath">The module containing the managed PDB identity.</param>
    /// <param name="sourcePath">The exact document path from the managed PDB.</param>
    /// <returns>The stable source descriptor.</returns>
    internal DebugSourceInfo GetSourceInfo(string modulePath, string sourcePath)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        CorDebugLoadedModule? module = FindModule(modulePath);
        string moduleKey = module is null ? Path.GetFullPath(modulePath) : GetSourceModuleKey(module);
        return GetSourceInfo(module, moduleKey, sourcePath);
    }

    /// <summary>
    /// Resolves one stack-frame document by its stable session-local module identifier.
    /// </summary>
    /// <param name="moduleId">The module identifier returned by debugger module inspection.</param>
    /// <param name="sourcePath">The exact document path from the managed PDB.</param>
    /// <returns>The stable source descriptor.</returns>
    internal DebugSourceInfo GetSourceInfo(int moduleId, string sourcePath)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        CorDebugLoadedModule module = FindModule(moduleId)
            ?? throw new KeyNotFoundException($"Debugger module {moduleId} is no longer loaded.");
        return GetSourceInfo(module, GetSourceModuleKey(module), sourcePath);
    }

    private DebugSourceInfo GetSourceInfo(
        CorDebugLoadedModule? module,
        string moduleKey,
        string sourcePath)
    {
        string key = CreateSourceKey(moduleKey, sourcePath);
        if (_sources.TryGetValue(key, out DebugSourceRegistration? existing))
        {
            return existing.Info;
        }

        using DebugSymbolReader? symbols = module is null
            ? DebugSymbolReader.TryOpen(moduleKey)
            : OpenSymbols(module);
        if (symbols is not null)
        {
            foreach (ManagedSymbolDocument document in symbols.GetDocuments().Where(
                document => PathsEqual(document.Path, sourcePath)))
            {
                return RegisterSource(moduleKey, document).Info;
            }
        }

        return RegisterUnavailableSource(moduleKey, sourcePath).Info;
    }

    /// <summary>
    /// Replaces build-time to local source mappings before runtime binding.
    /// </summary>
    /// <param name="mappings">The complete source path mapping dictionary.</param>
    /// <param name="sourceLinkOptions">The complete Source Link URL policy.</param>
    /// <param name="symbolOptions">The complete trusted symbol search policy.</param>
    internal void SetSourceOptions(
        IReadOnlyDictionary<string, string> mappings,
        IReadOnlyDictionary<string, bool> sourceLinkOptions,
        DebugSymbolOptions symbolOptions)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        _sourcePathMapper.Set(mappings);
        _sourceLinkPolicy.Set(sourceLinkOptions);
        _symbolLocator.Set(symbolOptions);
        ClearSources();
    }

    private DebugSourceRegistration RegisterSource(
        string modulePath,
        ManagedSymbolDocument document)
    {
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

    private static string CreateSourceKey(string moduleKey, string sourcePath) =>
        $"{moduleKey}\0{sourcePath}";

    private static string GetSourceModuleKey(CorDebugLoadedModule module) =>
        module.Path ?? $"in-memory:{module.Id}";

    private void ClearSources()
    {
        _sources.Clear();
        _sourcesByReference.Clear();
    }
}
