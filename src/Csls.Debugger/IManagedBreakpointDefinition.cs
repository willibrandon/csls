namespace Csls.Debugger;

/// <summary>
/// Exposes stop predicates shared by managed runtime breakpoint kinds.
/// </summary>
internal interface IManagedBreakpointDefinition
{
    /// <summary>
    /// Gets the optional source-language Boolean condition.
    /// </summary>
    string? Condition { get; }

    /// <summary>
    /// Gets the optional interpolated message that replaces stopping.
    /// </summary>
    string? LogMessage { get; }

    /// <summary>
    /// Records one condition-matching hit and evaluates its hit-count predicate.
    /// </summary>
    /// <returns>True when the hit-count predicate permits the breakpoint action.</returns>
    bool RegisterHit();
}
