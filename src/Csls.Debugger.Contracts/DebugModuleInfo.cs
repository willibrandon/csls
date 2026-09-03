namespace Csls.Debugger.Contracts;

/// <summary>
/// Describes one managed module loaded in the debug target.
/// </summary>
/// <param name="Id">The stable session-local module identifier.</param>
/// <param name="Name">The module display name.</param>
/// <param name="Path">The absolute module path when the runtime exposes one.</param>
/// <param name="SymbolKind">The validated loaded symbol format.</param>
/// <param name="SymbolPath">The associated Portable PDB path when available.</param>
public sealed record DebugModuleInfo(
    int Id,
    string Name,
    string? Path,
    DebugModuleSymbolKind SymbolKind,
    string? SymbolPath);
