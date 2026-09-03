namespace Csls.Debugger.Contracts;

/// <summary>
/// Describes one managed module loaded in the debug target.
/// </summary>
/// <param name="Id">The stable session-local module identifier.</param>
/// <param name="Name">The module display name.</param>
/// <param name="Path">The absolute module path when the runtime exposes one.</param>
/// <param name="SymbolKind">The validated loaded symbol format.</param>
/// <param name="SymbolPath">The associated Portable PDB path when available.</param>
/// <param name="IsOptimized">Whether the runtime permits optimized JIT code, when known.</param>
/// <param name="OptimizationDiagnostic">A bounded JIT-policy diagnostic when one exists.</param>
/// <param name="IsUserCode">Whether the module is classified as user code, when known.</param>
/// <param name="JustMyCodeDiagnostic">A bounded JMC-policy diagnostic when one exists.</param>
public sealed record DebugModuleInfo(
    int Id,
    string Name,
    string? Path,
    DebugModuleSymbolKind SymbolKind,
    string? SymbolPath,
    bool? IsOptimized,
    string? OptimizationDiagnostic,
    bool? IsUserCode,
    string? JustMyCodeDiagnostic);
