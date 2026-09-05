namespace Csls.Debugger.Contracts;

/// <summary>
/// Carries the complete replacement set of managed function breakpoints.
/// </summary>
/// <param name="Breakpoints">The ordered function-breakpoint requests.</param>
public sealed record DebugFunctionBreakpointSetRequest(
    IReadOnlyList<DebugFunctionBreakpointRequest> Breakpoints);
