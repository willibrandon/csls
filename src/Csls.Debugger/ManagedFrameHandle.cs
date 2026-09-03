using Csls.Debugger.Contracts;

namespace Csls.Debugger;

/// <summary>
/// Retains one runtime frame pointer and its stop-generation identity.
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
}
