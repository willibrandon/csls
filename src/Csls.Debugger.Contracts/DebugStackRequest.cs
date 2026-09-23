namespace Csls.Debugger.Contracts;

/// <summary>
/// Selects a bounded page of frames from one managed thread.
/// </summary>
/// <param name="ThreadId">The session-local managed thread identifier.</param>
/// <param name="StartFrame">The zero-based first frame.</param>
/// <param name="Levels">The maximum frame count, or zero for all remaining frames.</param>
public sealed record DebugStackRequest(int ThreadId, int StartFrame, int Levels)
{
    /// <summary>
    /// Gets the optional request-scoped progress receiver marshaled by private debugger RPC.
    /// </summary>
    public IProgress<DebugStackWalkProgress>? Progress { get; init; }
}
