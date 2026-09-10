namespace Csls.DebugAdapter;

/// <summary>
/// Carries one normalized inclusive source range for executable-location discovery.
/// </summary>
/// <param name="SourcePath">The absolute source document path.</param>
/// <param name="StartLine">The one-based inclusive start line.</param>
/// <param name="StartColumn">The one-based inclusive start column.</param>
/// <param name="EndLine">The one-based inclusive end line.</param>
/// <param name="EndColumn">The one-based inclusive end column.</param>
internal readonly record struct BreakpointLocationRange(
    string SourcePath,
    int StartLine,
    int StartColumn,
    int EndLine,
    int EndColumn);
