namespace Csls.Debugger.Contracts;

/// <summary>
/// Replaces the complete managed exception breakpoint policy.
/// </summary>
/// <param name="Breakpoints">The ordered exception breakpoint filters.</param>
public sealed record DebugExceptionBreakpointSetRequest(
    IReadOnlyList<DebugExceptionBreakpointRequest> Breakpoints);
