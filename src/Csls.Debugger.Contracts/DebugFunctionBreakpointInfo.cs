namespace Csls.Debugger.Contracts;

/// <summary>
/// Describes the current binding state of one managed function breakpoint.
/// </summary>
/// <param name="Id">The stable session-local breakpoint identifier.</param>
/// <param name="Name">The requested managed function name.</param>
/// <param name="Verified">Whether at least one runtime method is bound.</param>
/// <param name="Message">An optional explanation when no method is bound.</param>
/// <param name="Condition">The optional source-language Boolean condition.</param>
/// <param name="HitCondition">The normalized hit-count predicate when valid.</param>
public sealed record DebugFunctionBreakpointInfo(
    int Id,
    string Name,
    bool Verified,
    string? Message,
    string? Condition,
    string? HitCondition);
