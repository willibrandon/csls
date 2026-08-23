using Microsoft.CodeAnalysis;
using System.Collections.Concurrent;

namespace Csls.Workspaces;

/// <summary>
/// Indexes generated documents by mapped Razor path for one immutable Roslyn project.
/// </summary>
internal sealed class RazorMappingProjectCache
{
    /// <summary>
    /// Initializes the path index using platform file-system comparison rules.
    /// </summary>
    internal RazorMappingProjectCache()
    {
        StringComparer pathComparer = OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;
        Documents = new ConcurrentDictionary<string, SourceGeneratedDocument>(pathComparer);
    }

    /// <summary>
    /// Gets generated documents indexed by their mapped Razor paths.
    /// </summary>
    internal ConcurrentDictionary<string, SourceGeneratedDocument> Documents { get; }
}
