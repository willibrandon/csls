namespace Csls.Debugger;

/// <summary>
/// Retains one runtime module for managed-IL breakpoint rebinding.
/// </summary>
internal sealed class InstructionBreakpointModule
{
    /// <summary>
    /// Gets the normalized absolute module path when available.
    /// </summary>
    internal required string? Path { get; init; }

    /// <summary>
    /// Gets the owned ICorDebugModule pointer.
    /// </summary>
    internal required nint Pointer { get; init; }

    /// <summary>
    /// Gets the owned canonical module identity.
    /// </summary>
    internal required nint Identity { get; init; }
}
