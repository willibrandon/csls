namespace Csls.Debugger.Contracts;

/// <summary>
/// Describes one managed exception-stage and type filter.
/// </summary>
/// <param name="BreakMode">The managed exception stage to observe.</param>
/// <param name="ExceptionTypeNames">The exact or base type names, or an empty list for every type.</param>
public sealed record DebugExceptionBreakpointRequest(
    DebugExceptionBreakMode BreakMode,
    IReadOnlyList<string> ExceptionTypeNames);
