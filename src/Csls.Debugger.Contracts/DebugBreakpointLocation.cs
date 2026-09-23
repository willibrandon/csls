namespace Csls.Debugger.Contracts;

/// <summary>
/// Describes one executable source location reported by loaded symbols.
/// </summary>
/// <param name="Line">The one-based source start line.</param>
/// <param name="Column">The one-based UTF-16 source start column.</param>
/// <param name="EndLine">The one-based source end line.</param>
/// <param name="EndColumn">The one-based UTF-16 source end column.</param>
public sealed record DebugBreakpointLocation(
    int Line,
    int Column,
    int EndLine,
    int EndColumn);
