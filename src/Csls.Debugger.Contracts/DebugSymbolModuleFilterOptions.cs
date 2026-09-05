namespace Csls.Debugger.Contracts;

/// <summary>
/// Filters eager symbol lookup by managed module file name.
/// </summary>
public sealed class DebugSymbolModuleFilterOptions
{
    /// <summary>
    /// Gets the inclusion or exclusion filtering mode.
    /// </summary>
    public DebugSymbolModuleFilterMode Mode { get; init; }

    /// <summary>
    /// Gets case-insensitive wildcard patterns excluded in load-all mode.
    /// </summary>
    public IReadOnlyList<string> ExcludedModules { get; init; } = [];

    /// <summary>
    /// Gets case-insensitive wildcard patterns included in load-only mode.
    /// </summary>
    public IReadOnlyList<string> IncludedModules { get; init; } = [];

    /// <summary>
    /// Gets whether adjacent or embedded symbols remain eligible for non-included modules.
    /// </summary>
    public bool IncludeSymbolsNextToModules { get; init; } = true;
}
