namespace Csls.Debugger;

/// <summary>
/// Identifies one recognized managed runtime breakpoint callback.
/// </summary>
/// <param name="Kind">The logical breakpoint kind.</param>
/// <param name="Definition">The logical definition that owns stop predicates.</param>
internal sealed record ManagedBreakpointHit(
    DebugBreakpointKind Kind,
    IManagedBreakpointDefinition Definition);
