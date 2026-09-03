namespace Csls.Debugger.Contracts;

/// <summary>
/// Describes one requested managed-IL instruction breakpoint.
/// </summary>
/// <param name="InstructionReference">The opaque or virtual managed-IL reference.</param>
/// <param name="Offset">The signed byte offset from the reference.</param>
/// <param name="HitCondition">The optional hit-count expression.</param>
public sealed record DebugInstructionBreakpointRequest(
    string InstructionReference,
    long Offset = 0,
    string? HitCondition = null);
