namespace Csls.Debugger.Contracts;

/// <summary>
/// Defines managed-IL breakpoint operations exposed through private debugger control RPC.
/// </summary>
public partial interface IDebuggerControlTarget
{
    /// <summary>
    /// Replaces every managed-IL instruction breakpoint.
    /// </summary>
    /// <param name="request">The complete replacement set.</param>
    /// <param name="cancellationToken">Cancels breakpoint binding.</param>
    /// <returns>The ordered current breakpoint states.</returns>
    Task<IReadOnlyList<DebugInstructionBreakpointInfo>> SetInstructionBreakpointsAsync(
        DebugInstructionBreakpointSetRequest request,
        CancellationToken cancellationToken);
}
