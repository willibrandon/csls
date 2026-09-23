namespace Csls.Debugger.Contracts;

/// <summary>
/// Describes one requested managed function breakpoint before runtime binding.
/// </summary>
/// <param name="Name">The method name or fully qualified type-and-method name.</param>
/// <param name="Condition">The optional source-language Boolean condition.</param>
/// <param name="HitCondition">The optional hit-count expression.</param>
public sealed record DebugFunctionBreakpointRequest(
    string Name,
    string? Condition = null,
    string? HitCondition = null);
