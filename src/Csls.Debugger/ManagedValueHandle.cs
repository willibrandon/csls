using Csls.Debugger.Contracts;

namespace Csls.Debugger;

/// <summary>
/// Retains one expandable managed value for a single debugger stop generation.
/// </summary>
internal sealed class ManagedValueHandle
{
    /// <summary>
    /// Gets or initializes the session-local variable-container identifier.
    /// </summary>
    internal required int Id { get; init; }

    /// <summary>
    /// Gets or initializes the stop generation that owns the value.
    /// </summary>
    internal required DebugStopGeneration Generation { get; init; }

    /// <summary>
    /// Gets or initializes the frame context used to evaluate source assignments.
    /// </summary>
    internal int? FrameId { get; init; }

    /// <summary>
    /// Gets or initializes the owned ICorDebugValue pointer.
    /// </summary>
    internal required nint Pointer { get; init; }

    /// <summary>
    /// Gets or initializes the owned canonical COM identity pointer.
    /// </summary>
    internal required nint Identity { get; init; }

    /// <summary>
    /// Gets or initializes the presentation view used to expand the value.
    /// </summary>
    internal ManagedValueView View { get; init; }

    /// <summary>
    /// Gets or initializes the opaque stopped-state memory handle.
    /// </summary>
    internal string? MemoryReference { get; init; }

    /// <summary>
    /// Gets or initializes the managed address represented by the memory handle.
    /// </summary>
    internal ulong MemoryAddress { get; init; }

    /// <summary>
    /// Gets or initializes the canonical source expression for child values.
    /// </summary>
    internal string? EvaluateName { get; init; }
}
