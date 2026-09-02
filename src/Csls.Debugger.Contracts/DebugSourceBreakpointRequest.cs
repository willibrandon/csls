namespace Csls.Debugger.Contracts;

/// <summary>
/// Describes one requested source breakpoint before runtime binding.
/// </summary>
/// <param name="Line">The one-based requested source line.</param>
/// <param name="Column">The optional one-based requested source column.</param>
public sealed record DebugSourceBreakpointRequest(int Line, int? Column);
