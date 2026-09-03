namespace Csls.Debugger;

/// <summary>
/// Owns one activated runtime binding for a logical function breakpoint.
/// </summary>
internal sealed class FunctionBreakpointBinding
{
    /// <summary>
    /// Gets the logical function breakpoint identifier.
    /// </summary>
    internal required int BreakpointId { get; init; }

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
