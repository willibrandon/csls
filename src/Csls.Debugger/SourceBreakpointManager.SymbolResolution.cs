using Csls.Debugger.Interop;

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
        return module is null
            ? PortablePdbReader.TryOpen(modulePath)
            : OpenSymbols(module);
    }

    private static PortablePdbReader? OpenSymbols(CorDebugLoadedModule module) =>
        module.SymbolImage is not null
            ? PortablePdbReader.TryOpen(module.SymbolImage)
            : module.Path is null
                ? null
                : PortablePdbReader.TryOpen(module.Path, module.SymbolPath);

    /// <summary>
    /// Gets the identity-validated associated PDB selected for one loaded module.
    /// </summary>
    /// <param name="modulePath">The absolute managed module path.</param>
    /// <returns>The selected associated PDB path, or null for embedded or unavailable symbols.</returns>
    internal string? GetSymbolPath(string modulePath) => FindModule(modulePath)?.SymbolPath;

    /// <summary>
    /// Resolves a borrowed runtime module to its retained session record.
    /// </summary>
    /// <param name="module">The borrowed ICorDebugModule pointer.</param>
    /// <returns>The retained module, or null when no load callback was observed.</returns>
    internal CorDebugLoadedModule? FindModule(nint module)
    {
        ArgumentOutOfRangeException.ThrowIfZero(module);
        nint identity = ComAbi.QueryInterface(module, s_iUnknownInterfaceId);
        try
        {
            return _modules.GetValueOrDefault(identity);
        }
        finally
        {
            _ = ComAbi.Release(identity);
        }
    }

    private CorDebugLoadedModule? FindModule(string modulePath) => _modules.Values.FirstOrDefault(
        candidate => candidate.Path is not null && PathsEqual(candidate.Path, modulePath));

    private CorDebugLoadedModule? FindModule(int moduleId) => _modules.Values.FirstOrDefault(
        candidate => candidate.Id == moduleId);
}
