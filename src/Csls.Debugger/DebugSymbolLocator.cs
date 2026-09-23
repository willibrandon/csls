using Csls.Debugger.Contracts;

namespace Csls.Debugger;

/// <summary>
/// Resolves and caches identity-matched managed PDBs from configured locations.
/// </summary>
internal sealed partial class DebugSymbolLocator
{
    private static readonly Uri s_microsoftServer =
        new("https://msdl.microsoft.com/download/symbols/");
    private static readonly Uri s_nugetServer =
        new("https://symbols.nuget.org/download/symbols/");
    private readonly List<string> _directories = [];
    private readonly List<Uri> _servers = [];
    private readonly SymbolModuleFilter _moduleFilter = new();
    private string _cachePath = GetDefaultCachePath();

    /// <summary>
    /// Replaces the complete trusted symbol search policy before target activation.
    /// </summary>
    /// <param name="options">The local paths, servers, and cache selection.</param>
    internal void Set(DebugSymbolOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _directories.Clear();
        _servers.Clear();
        foreach (string searchPath in options.SearchPaths)
        {
            AddSearchPath(searchPath);
        }

        if (options.SearchMicrosoftSymbolServer)
        {
            _servers.Add(s_microsoftServer);
        }

        if (options.SearchNuGetOrgSymbolServer)
        {
            _servers.Add(s_nugetServer);
        }

        _cachePath = options.CachePath is null
            ? GetDefaultCachePath()
            : NormalizeAbsolutePath(options.CachePath, "symbolOptions.cachePath");
        _moduleFilter.Set(options.ModuleFilter);
    }

    /// <summary>
    /// Resolves one matching managed PDB without accepting unvalidated cache content.
    /// </summary>
    /// <param name="modulePath">The absolute managed PE path.</param>
    /// <param name="cancellationToken">Cancels symbol-server retrieval.</param>
    /// <returns>The selected storage location, or null when symbols are unavailable.</returns>
    internal async Task<DebugSymbolResolution?> ResolveAsync(
        string modulePath,
        CancellationToken cancellationToken)
    {
        if (_moduleFilter.AllowsAdjacent(modulePath))
        {
            using DebugSymbolReader? adjacent = TryOpen(modulePath, symbolPath: null);
            if (adjacent is not null)
            {
                return new DebugSymbolResolution(adjacent.StorageKind, adjacent.Path);
            }
        }

        if (!_moduleFilter.AllowsSearch(modulePath))
        {
            return null;
        }

        CodeViewSymbolReference? reference = TryReadReference(modulePath);
        if (reference is null)
        {
            return null;
        }

        string? localMatch = _directories
            .Select(directory => FindLocalMatch(modulePath, reference, directory))
            .FirstOrDefault(static match => match is not null);
        if (localMatch is not null)
        {
            return new DebugSymbolResolution(DebugSymbolStorageKind.AssociatedFile, localMatch);
        }

        foreach (string identity in GetIdentities(reference))
        {
            string cacheFile = GetStorePath(_cachePath, reference.FileName, identity);
            if (IsMatch(modulePath, cacheFile))
            {
                return new DebugSymbolResolution(DebugSymbolStorageKind.AssociatedFile, cacheFile);
            }

            foreach (Uri server in _servers)
            {
                string? cached = await DownloadAndCacheAsync(
                    server,
                    modulePath,
                    cacheFile,
                    GetStoreIndex(reference.FileName, identity),
                    cancellationToken).ConfigureAwait(false);
                if (cached is not null)
                {
                    return new DebugSymbolResolution(DebugSymbolStorageKind.AssociatedFile, cached);
                }
            }
        }

        return null;
    }
}
