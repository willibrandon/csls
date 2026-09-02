namespace Csls.Debugger.Contracts;

/// <summary>
/// Carries a paged managed stack and its complete frame count.
/// </summary>
/// <param name="StackFrames">The requested ordered frame page.</param>
/// <param name="TotalFrames">The number of frames before paging.</param>
public sealed record DebugStackTrace(
    IReadOnlyList<DebugStackFrameInfo> StackFrames,
    int TotalFrames);
