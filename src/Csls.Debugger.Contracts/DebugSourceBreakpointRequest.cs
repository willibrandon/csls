namespace Csls.Debugger.Contracts;

/// <summary>
/// Describes one requested source breakpoint before runtime binding.
/// </summary>
/// <param name="Line">The one-based requested source line.</param>
/// <param name="Column">The optional one-based requested source column.</param>
/// <param name="Condition">The optional source-language Boolean condition.</param>
/// <param name="HitCondition">The optional hit-count expression.</param>
/// <param name="LogMessage">The optional interpolated message that replaces stopping.</param>
public sealed record DebugSourceBreakpointRequest(
    int Line,
    int? Column,
    string? Condition = null,
    string? HitCondition = null,
    string? LogMessage = null);
