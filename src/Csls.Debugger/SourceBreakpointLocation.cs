namespace Csls.Debugger;

/// <summary>
/// Identifies one executable managed-symbol sequence point.
/// </summary>
/// <param name="MethodToken">The defining method metadata token.</param>
/// <param name="IlOffset">The IL instruction offset.</param>
/// <param name="Line">The resolved one-based source line.</param>
/// <param name="Column">The resolved one-based source column.</param>
/// <param name="EndLine">The inclusive one-based final source line.</param>
internal sealed record SourceBreakpointLocation(
    uint MethodToken,
    uint IlOffset,
    int Line,
    int Column,
    int EndLine);
