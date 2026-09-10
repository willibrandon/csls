namespace Csls.Debugger.Contracts;

/// <summary>
/// Selects a current frame and source position for safe instruction-pointer destinations.
/// </summary>
/// <param name="FrameId">The generation-bound active frame identifier.</param>
/// <param name="SourcePath">The absolute client source path.</param>
/// <param name="Line">The one-based requested source line.</param>
/// <param name="Column">The optional one-based requested source column.</param>
public sealed record DebugGotoTargetsRequest(
    int FrameId,
    string SourcePath,
    int Line,
    int? Column);
