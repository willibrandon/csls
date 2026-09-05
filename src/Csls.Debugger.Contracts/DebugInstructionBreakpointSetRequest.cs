namespace Csls.Debugger.Contracts;

/// <summary>
/// Replaces every managed-IL instruction breakpoint in one debugger session.
/// </summary>
/// <param name="Breakpoints">The ordered complete replacement set.</param>
public sealed record DebugInstructionBreakpointSetRequest(
    IReadOnlyList<DebugInstructionBreakpointRequest> Breakpoints);
