namespace Csls.Debugger.Contracts;

/// <summary>
/// Describes the managed exception responsible for the current debugger stop.
/// </summary>
/// <param name="ExceptionId">The fully qualified managed exception type name.</param>
/// <param name="Description">The debugger-readable exception summary.</param>
/// <param name="BreakMode">The exception stage that matched debugger policy.</param>
public sealed record DebugExceptionInfo(
    string ExceptionId,
    string Description,
    DebugExceptionBreakMode BreakMode);
