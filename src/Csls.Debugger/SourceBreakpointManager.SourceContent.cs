using Csls.Debugger.Contracts;

namespace Csls.Debugger;

/// <summary>
/// Catalogs checksummed source documents and session-local embedded content.
/// </summary>
internal sealed partial class SourceBreakpointManager
{
    private readonly Dictionary<string, DebugSourceRegistration> _sources =
        new(PathComparer);
    private readonly Dictionary<int, DebugSourceRegistration> _sourcesByReference = [];
    private readonly SourceLinkPolicy _sourceLinkPolicy = new();
    private readonly SourcePathMapper _sourcePathMapper = new();
    private bool _requireExactSource = true;
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
            return existing.Document is ManagedSymbolDocument document
                ? RegisterSource(moduleKey, document).Info
                : RegisterUnavailableSource(moduleKey, sourcePath).Info;
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
    /// <param name="requireExactSource">Whether local source must match the symbols before use.</param>
    internal void SetSourceOptions(
        IReadOnlyDictionary<string, string> mappings,
        IReadOnlyDictionary<string, bool> sourceLinkOptions,
        DebugSymbolOptions symbolOptions,
        bool requireExactSource)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        _sourcePathMapper.Set(mappings);
        _sourceLinkPolicy.Set(sourceLinkOptions);
        _symbolLocator.Set(symbolOptions);
        _requireExactSource = requireExactSource;
        ClearSources();
    }

    private DebugSourceRegistration RegisterSource(
        string modulePath,
        ManagedSymbolDocument document)
    {
        string key = CreateSourceKey(modulePath, document.Path);
        _ = _sources.TryGetValue(key, out DebugSourceRegistration? existing);
        string resolvedPath = _sourcePathMapper.Map(document.Path);
        LocalSourceStatus localStatus = document.EmbeddedSource is null
            ? SourceChecksumVerifier.InspectFile(resolvedPath, document.Checksum)
            : LocalSourceStatus.Unavailable;
        bool localSourceIsCurrent = localStatus == LocalSourceStatus.Verified;
        bool useUnverifiedLocalSource = !_requireExactSource &&
            localStatus is LocalSourceStatus.Unverified or LocalSourceStatus.Mismatch;
        bool useSourceLink = document.EmbeddedSource is null &&
            !localSourceIsCurrent &&
            !useUnverifiedLocalSource &&
            document.Checksum is not null &&
            document.SourceLinkUri is not null;
        int sourceReference = document.EmbeddedSource is null && !useSourceLink
            ? 0
            : existing is { SourceReference: > 0 } ? existing.SourceReference : checked(++_nextSourceReference);
        var info = new DebugSourceInfo(
                GetPortableFileName(document.Path),
                document.EmbeddedSource is not null || localSourceIsCurrent || useUnverifiedLocalSource
                    ? resolvedPath
                    : null,
                sourceReference,
                document.EmbeddedSource is not null
                    ? "embedded source"
                    : localSourceIsCurrent
                        ? null
                        : useUnverifiedLocalSource
                            ? "unverified local source (requireExactSource=false)"
                            : useSourceLink
                                ? "Source Link"
                                : "original source is unavailable or does not match its checksum",
                useUnverifiedLocalSource ? null : document.Checksum);
        DebugSourceRegistration registration = existing ?? new DebugSourceRegistration
        {
            Info = info,
            Document = document,
            Content = document.EmbeddedSource is null
                ? null
                : new DebugSourceContent(
                    SourceTextDecoder.Decode(document.EmbeddedSource),
                    GetMimeType(GetPortableFileName(document.Path)))
        };
        registration.Info = info;
        _sources[key] = registration;
        if (sourceReference > 0)
        {
            registration.SourceReference = sourceReference;
            _ = _sourcesByReference.TryAdd(sourceReference, registration);
        }

        return registration;
    }

    private DebugSourceRegistration RegisterUnavailableSource(
        string modulePath,
        string sourcePath)
    {
        string key = CreateSourceKey(modulePath, sourcePath);
        string resolvedPath = _sourcePathMapper.Map(sourcePath);
        bool useLocalSource = !_requireExactSource &&
            SourceChecksumVerifier.InspectFile(resolvedPath, checksum: null) == LocalSourceStatus.Unverified;
        var registration = new DebugSourceRegistration
        {
            Info = new DebugSourceInfo(
                GetPortableFileName(sourcePath),
                useLocalSource ? resolvedPath : null,
                SourceReference: 0,
                Origin: useLocalSource ? "unverified local source (requireExactSource=false)" : "source is unavailable",
                Checksum: null)
        };
        _sources[key] = registration;
        return registration;
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
