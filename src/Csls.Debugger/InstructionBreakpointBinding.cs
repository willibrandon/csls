namespace Csls.Debugger;

/// <summary>
/// Owns one activated runtime binding for a logical managed-IL breakpoint.
/// </summary>
internal sealed class InstructionBreakpointBinding
{
    /// <summary>
    /// Gets the logical definition that owns hit-count state.
    /// </summary>
    internal required InstructionBreakpointDefinition Definition { get; init; }

    /// <summary>
    /// Gets the canonical module identity that owns the code.
    /// </summary>
    internal required nint ModuleIdentity { get; init; }

    /// <summary>
    /// Gets the owned ICorDebugFunctionBreakpoint pointer.
    /// </summary>
    internal required nint Breakpoint { get; init; }

    /// <summary>
    /// Gets the owned canonical breakpoint identity.
    /// </summary>
    internal required nint Identity { get; init; }
}
