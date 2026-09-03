using Csls.Debugger.Contracts;

namespace Csls.Mcp.Worker;

/// <summary>
/// Carries one current-generation managed stack page.
/// </summary>
/// <param name="DebugSession">The explicit debugger-session identifier.</param>
/// <param name="StopGeneration">The stop generation that owns the frames.</param>
/// <param name="StackFrames">The requested ordered frame page.</param>
/// <param name="TotalFrames">The complete frame count before paging.</param>
internal sealed record McpDebugStackResult(
    string DebugSession,
    long StopGeneration,
    IReadOnlyList<DebugStackFrameInfo> StackFrames,
    int TotalFrames);
