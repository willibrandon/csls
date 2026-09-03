using Csls.Debugger.Contracts;

namespace Csls.Debugger;

/// <summary>
/// Owns a loaded module pointer and its canonical COM identity.
/// </summary>
internal sealed class CorDebugLoadedModule
{
    /// <summary>
    /// Gets the stable session-local module identifier.
    /// </summary>
    internal required int Id { get; init; }

    /// <summary>
    /// Gets the absolute module path when the runtime exposes one.
    /// </summary>
    internal required string? Path { get; init; }

    /// <summary>
    /// Gets the owned ICorDebugModule pointer.
    /// </summary>
    internal required nint Pointer { get; init; }

    /// <summary>
    /// Gets the owned canonical IUnknown identity pointer.
    /// </summary>
    internal required nint Identity { get; init; }

    /// <summary>
    /// Gets the validated symbol format discovered when the module loaded.
    /// </summary>
    internal DebugModuleSymbolKind SymbolKind { get; set; }

    /// <summary>
    /// Gets the associated Portable PDB path when symbols are stored separately.
    /// </summary>
    internal string? SymbolPath { get; set; }

    /// <summary>
    /// Gets or sets whether symbol discovery has completed for this module.
    /// </summary>
    internal bool SymbolsInspected { get; set; }

    /// <summary>
    /// Gets whether the runtime permits optimized JIT code, when known.
    /// </summary>
    internal bool? IsOptimized { get; init; }

    /// <summary>
    /// Gets a bounded diagnostic when the requested JIT policy could not be established.
    /// </summary>
    internal string? OptimizationDiagnostic { get; init; }

    /// <summary>
    /// Gets or sets whether the module is classified as user code.
    /// </summary>
    internal bool? IsUserCode { get; set; }

    /// <summary>
    /// Gets or sets a bounded diagnostic when runtime JMC configuration fails.
    /// </summary>
    internal string? JustMyCodeDiagnostic { get; set; }

    /// <summary>
    /// Gets or sets whether runtime JMC configuration has completed for this module.
    /// </summary>
    internal bool JustMyCodeConfigured { get; set; }
}
