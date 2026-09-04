using Csls.Debugger.Contracts;
using System.Reflection.PortableExecutable;

namespace Csls.Debugger;

/// <summary>
/// Retains a current-generation native binding for one logical application-level stopped frame.
/// </summary>
internal sealed class ManagedFrameHandle
{
    /// <summary>
    /// Gets or initializes the session-local DAP frame identifier.
    /// </summary>
    internal required int Id { get; init; }

    /// <summary>
    /// Gets or initializes the stop generation that owns the frame.
    /// </summary>
    internal required DebugStopGeneration Generation { get; init; }

    /// <summary>
    /// Gets or initializes the owned ICorDebugFrame pointer.
    /// </summary>
    internal required nint Pointer { get; init; }

    /// <summary>
    /// Gets or initializes the managed thread that owns the frame.
    /// </summary>
    internal required int ThreadId { get; init; }

    /// <summary>
    /// Gets or initializes the zero-based position in the managed stack.
    /// </summary>
    internal required int FrameIndex { get; init; }

    /// <summary>
    /// Gets or initializes the physical frame stack-range start.
    /// </summary>
    internal required ulong StackStart { get; init; }

    /// <summary>
    /// Gets or initializes the physical frame stack-range end.
    /// </summary>
    internal required ulong StackEnd { get; init; }

    /// <summary>
    /// Gets or initializes the method-definition metadata token when available.
    /// </summary>
    internal required uint MethodToken { get; init; }

    /// <summary>
    /// Gets or initializes the current IL instruction offset when available.
    /// </summary>
    internal required uint IlOffset { get; init; }

    /// <summary>
    /// Gets or initializes the loaded module path when available.
    /// </summary>
    internal string? ModulePath { get; init; }

    /// <summary>
    /// Gets or initializes the stable session-local module identifier when available.
    /// </summary>
    internal int? ModuleId { get; init; }

    /// <summary>
    /// Gets or initializes the immutable in-memory PE image when applicable.
    /// </summary>
    internal byte[]? ModuleImage { get; init; }

    /// <summary>
    /// Gets or initializes the immutable in-memory Portable PDB image when applicable.
    /// </summary>
    internal byte[]? SymbolImage { get; init; }

    /// <summary>
    /// Gets or initializes the Portable PDB deltas visible to this stopped frame.
    /// </summary>
    internal IReadOnlyList<byte[]> SymbolDeltas { get; init; } = [];

    /// <summary>
    /// Gets or initializes the selected associated PDB path when symbols are external.
    /// </summary>
    internal string? SymbolPath { get; init; }

    /// <summary>
    /// Gets or initializes the language-neutral managed method name.
    /// </summary>
    internal required string Name { get; init; }

    /// <summary>
    /// Gets or initializes the opaque stopped-state managed-IL reference.
    /// </summary>
    internal required string InstructionReference { get; init; }

    /// <summary>
    /// Gets or initializes the monotonic owner of numeric IL addresses for this native binding.
    /// </summary>
    internal required int InstructionAddressId { get; init; }

    /// <summary>
    /// Gets or initializes the source-language evaluator grammar.
    /// </summary>
    internal required DebugExpressionLanguage ExpressionLanguage { get; init; }

    /// <summary>
    /// Identifies one exact slot when this frame has a resolved runtime module and method.
    /// </summary>
    /// <param name="kind">The argument or local collection containing the slot.</param>
    /// <param name="index">The physical zero-based slot index.</param>
    /// <returns>The exact physical origin, or null when its stack range, module, or method is unavailable.</returns>
    internal ManagedValueOrigin? CreateValueOrigin(ManagedScopeKind kind, int index) =>
        ModuleId is int moduleId && MethodToken != 0 && StackStart != 0 && StackEnd != 0
            ? new ManagedFrameValueOrigin(
                ThreadId, StackStart, StackEnd, moduleId, MethodToken, kind, index)
            : null;

    /// <summary>
    /// Opens an owned PE reader over the file or immutable in-memory module image.
    /// </summary>
    /// <returns>An owned PE reader, or null when the module image is unavailable.</returns>
    internal PEReader? OpenPeReader() => ModuleImage is not null
        ? new PEReader(new MemoryStream(ModuleImage, writable: false))
        : ModulePath is not null && File.Exists(ModulePath)
            ? new PEReader(new FileStream(
                ModulePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read | FileShare.Delete))
            : null;

    /// <summary>
    /// Opens an owned managed-symbol reader over the selected frame symbols.
    /// </summary>
    /// <returns>An owned symbol reader, or null when symbols are unavailable.</returns>
    internal DebugSymbolReader? OpenSymbols() => SymbolImage is not null
        ? DebugSymbolReader.TryOpen(SymbolImage, SymbolDeltas)
        : ModulePath is null
            ? null
            : DebugSymbolReader.TryOpen(ModulePath, SymbolPath, SymbolDeltas);
}
