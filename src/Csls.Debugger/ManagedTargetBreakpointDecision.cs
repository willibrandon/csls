namespace Csls.Debugger;

/// <summary>
/// Selects callback handling for a temporary source-aware Step Into breakpoint.
/// </summary>
internal enum ManagedTargetBreakpointDecision
{
    /// <summary>
    /// The breakpoint does not belong to the active targeted step.
    /// </summary>
    Unrecognized,

    /// <summary>
    /// An earlier invocation was skipped and execution must continue.
    /// </summary>
    Continue,

    /// <summary>
    /// The selected callee was reached and execution must remain stopped.
    /// </summary>
    Stopped
}
