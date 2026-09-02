namespace Csls.Debugger.Contracts;

/// <summary>
/// Describes the current binding state of one source breakpoint.
/// </summary>
/// <param name="Id">The stable session-local breakpoint identifier.</param>
/// <param name="SourcePath">The absolute source document path.</param>
/// <param name="Verified">Whether executable code is bound.</param>
/// <param name="Line">The requested or resolved one-based source line.</param>
/// <param name="Column">The requested or resolved one-based source column.</param>
/// <param name="Message">An optional explanation when the breakpoint is not verified.</param>
public sealed record DebugSourceBreakpointInfo(
    int Id,
    string SourcePath,
    bool Verified,
    int Line,
    int? Column,
    string? Message);
