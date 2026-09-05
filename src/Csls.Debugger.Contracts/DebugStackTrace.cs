namespace Csls.Debugger.Contracts;

/// <summary>
/// Carries a paged managed stack and its exact total when the stack end has been observed.
/// </summary>
/// <param name="StackFrames">The requested ordered frame page.</param>
/// <param name="TotalFrames">The exact number of frames before paging, or null while the total is unknown.</param>
public sealed record DebugStackTrace(
    IReadOnlyList<DebugStackFrameInfo> StackFrames,
    int? TotalFrames);
