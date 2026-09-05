namespace Csls.Debugger;

/// <summary>
/// Owns one activated runtime breakpoint and its logical source definition.
/// </summary>
internal sealed class SourceBreakpointBinding
{
    /// <summary>
    /// Gets the logical source breakpoint identifier.
    /// </summary>
    internal required int BreakpointId { get; init; }

    /// <summary>
    /// Gets the logical definition that owns the hit-count state.
    /// </summary>
    internal required SourceBreakpointDefinition Definition { get; init; }

    /// <summary>
    /// Gets the canonical identity of the module owning this binding.
    /// </summary>
    internal required nint ModuleIdentity { get; init; }

    /// <summary>
    /// Gets the owned ICorDebugFunctionBreakpoint pointer.
    /// </summary>
    internal required nint Breakpoint { get; init; }

    /// <summary>
    /// Gets the owned canonical IUnknown identity pointer.
    /// </summary>
    internal required nint Identity { get; init; }
}
