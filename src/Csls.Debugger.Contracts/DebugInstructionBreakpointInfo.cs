namespace Csls.Debugger.Contracts;

/// <summary>
/// Describes the current binding state of one managed-IL instruction breakpoint.
/// </summary>
/// <param name="Id">The stable session-local breakpoint identifier.</param>
/// <param name="Verified">Whether at least one runtime binding is active.</param>
/// <param name="InstructionReference">The requested instruction reference.</param>
/// <param name="Offset">The requested signed byte offset.</param>
/// <param name="Message">An optional validation or pending-binding diagnostic.</param>
/// <param name="HitCondition">The normalized hit-count predicate when valid.</param>
public sealed record DebugInstructionBreakpointInfo(
    int Id,
    bool Verified,
    string InstructionReference,
    long Offset,
    string? Message,
    string? HitCondition);
