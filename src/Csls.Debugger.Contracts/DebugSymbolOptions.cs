namespace Csls.Debugger.Contracts;

/// <summary>
/// Configures trusted locations used to resolve matching debugger symbols.
/// </summary>
public sealed class DebugSymbolOptions
{
    /// <summary>
    /// Gets absolute directories and HTTP symbol-server base URLs searched in order.
    /// </summary>
    public IReadOnlyList<string> SearchPaths { get; init; } = [];

    /// <summary>
    /// Gets whether the public Microsoft symbol server is searched.
    /// </summary>
    public bool SearchMicrosoftSymbolServer { get; init; }

    /// <summary>
    /// Gets whether the public NuGet.org symbol server is searched.
    /// </summary>
    public bool SearchNuGetOrgSymbolServer { get; init; }

    /// <summary>
    /// Gets the absolute downloaded-symbol cache directory, or null for the platform default.
    /// </summary>
    public string? CachePath { get; init; }

    /// <summary>
    /// Gets the module-name policy controlling eager symbol lookup.
    /// </summary>
    public DebugSymbolModuleFilterOptions ModuleFilter { get; init; } = new();
}
