namespace Csls.Debugger;

/// <summary>
/// Exposes the identity-validated symbol selection for loaded managed modules.
/// </summary>
internal sealed partial class SourceBreakpointManager
{
    /// <summary>
    /// Opens the identity-validated symbols selected for one loaded module.
    /// </summary>
    /// <param name="modulePath">The absolute managed module path.</param>
    /// <returns>An owned Portable PDB reader, or null when symbols are unavailable.</returns>
    internal PortablePdbReader? OpenSymbols(string modulePath)
    {
        CorDebugLoadedModule? module = FindModule(modulePath);
        return PortablePdbReader.TryOpen(modulePath, module?.SymbolPath);
    }

    /// <summary>
    /// Gets the identity-validated associated PDB selected for one loaded module.
    /// </summary>
    /// <param name="modulePath">The absolute managed module path.</param>
    /// <returns>The selected associated PDB path, or null for embedded or unavailable symbols.</returns>
    internal string? GetSymbolPath(string modulePath) => FindModule(modulePath)?.SymbolPath;

    private CorDebugLoadedModule? FindModule(string modulePath) => _modules.Values.FirstOrDefault(
        candidate => candidate.Path is not null && PathsEqual(candidate.Path, modulePath));
}
