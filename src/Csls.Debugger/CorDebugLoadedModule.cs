using Csls.Debugger.Contracts;
using System.Reflection.PortableExecutable;

namespace Csls.Debugger;

/// <summary>
/// Owns a loaded module pointer and its canonical COM identity.
/// </summary>
internal sealed class CorDebugLoadedModule
{
    /// <summary>
    /// Gets or initializes the runtime-reported module display name when available.
    /// </summary>
    internal string? Name { get; init; }

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
    /// Gets the associated managed PDB path when symbols are stored separately.
    /// </summary>
    internal string? SymbolPath { get; set; }

    /// <summary>
    /// Gets or sets an immutable Portable PDB image supplied by the runtime.
    /// </summary>
    internal byte[]? SymbolImage { get; set; }

    /// <summary>
    /// Gets the validated Portable PDB deltas in Hot Reload generation order.
    /// </summary>
    internal List<byte[]> SymbolDeltas { get; } = [];

    /// <summary>
    /// Gets the validated ECMA-335 metadata deltas in Hot Reload generation order.
    /// </summary>
    internal List<byte[]> MetadataDeltas { get; } = [];

    /// <summary>
    /// Gets or sets the number of Hot Reload generations committed to this module.
    /// </summary>
    internal int HotReloadGeneration { get; set; }

    /// <summary>
    /// Gets whether CoreCLR accepted Edit and Continue JIT policy for this module.
    /// </summary>
    internal bool? IsHotReloadEnabled { get; init; }

    /// <summary>
    /// Gets the bounded Hot Reload policy diagnostic when one exists.
    /// </summary>
    internal string? HotReloadDiagnostic { get; init; }

    /// <summary>
    /// Gets the compiler-facing Hot Reload capabilities of the target runtime.
    /// </summary>
    internal IReadOnlyList<string> HotReloadCapabilities { get; init; } = [];

    /// <summary>
    /// Gets whether CoreCLR reports that the module was loaded from memory.
    /// </summary>
    internal bool IsInMemory { get; init; }

    /// <summary>
    /// Gets whether CoreCLR reports that the module can grow through LoadClass callbacks.
    /// </summary>
    internal bool IsDynamic { get; init; }

    /// <summary>
    /// Gets or sets the immutable PE image copied from an in-memory module.
    /// </summary>
    internal byte[]? ModuleImage { get; set; }

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

    /// <summary>
    /// Opens an owned PE reader over the file or immutable in-memory module image.
    /// </summary>
    /// <returns>An owned PE reader, or null when the module image is unavailable.</returns>
    internal PEReader? OpenPeReader() => ModuleImage is not null
        ? new PEReader(new MemoryStream(ModuleImage, writable: false))
        : Path is not null && File.Exists(Path)
            ? new PEReader(new FileStream(
                Path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read | FileShare.Delete))
            : null;

    /// <summary>
    /// Opens the base symbols and all applied Hot Reload symbol generations.
    /// </summary>
    /// <returns>An owned symbol reader, or null when symbols are unavailable.</returns>
    internal DebugSymbolReader? OpenSymbols() => SymbolImage is not null
        ? DebugSymbolReader.TryOpen(SymbolImage, SymbolDeltas)
        : Path is null
            ? null
            : DebugSymbolReader.TryOpen(Path, SymbolPath, SymbolDeltas);
}
