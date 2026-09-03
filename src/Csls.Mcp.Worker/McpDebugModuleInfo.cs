using Csls.Debugger.Contracts;

namespace Csls.Mcp.Worker;

/// <summary>
/// Describes one MCP-facing managed module.
/// </summary>
/// <param name="Id">The stable session-local module identifier.</param>
/// <param name="Name">The module display name.</param>
/// <param name="Path">The absolute module path when available.</param>
/// <param name="SymbolKind">The validated symbol-format name.</param>
/// <param name="SymbolPath">The associated Portable PDB path when available.</param>
/// <param name="IsOptimized">Whether optimized JIT code is permitted, when known.</param>
/// <param name="OptimizationDiagnostic">The bounded JIT-policy diagnostic.</param>
/// <param name="IsUserCode">Whether the module is classified as user code.</param>
/// <param name="JustMyCodeDiagnostic">The bounded user-code diagnostic.</param>
internal sealed record McpDebugModuleInfo(
    int Id,
    string Name,
    string? Path,
    string SymbolKind,
    string? SymbolPath,
    bool? IsOptimized,
    string? OptimizationDiagnostic,
    bool? IsUserCode,
    string? JustMyCodeDiagnostic)
{
    /// <summary>
    /// Projects a private debugger module into the MCP contract.
    /// </summary>
    internal static McpDebugModuleInfo Create(DebugModuleInfo module) => new(
        module.Id,
        module.Name,
        module.Path,
        module.SymbolKind switch
        {
            DebugModuleSymbolKind.None => "none",
            DebugModuleSymbolKind.PortablePdb => "portablePdb",
            DebugModuleSymbolKind.EmbeddedPortablePdb => "embeddedPortablePdb",
            DebugModuleSymbolKind.InMemoryPortablePdb => "inMemoryPortablePdb",
            DebugModuleSymbolKind.WindowsPdb => "windowsPdb",
            _ => throw new InvalidDataException(
                $"Unknown debugger module symbol kind {module.SymbolKind}.")
        },
        module.SymbolPath,
        module.IsOptimized,
        module.OptimizationDiagnostic,
        module.IsUserCode,
        module.JustMyCodeDiagnostic);
}
