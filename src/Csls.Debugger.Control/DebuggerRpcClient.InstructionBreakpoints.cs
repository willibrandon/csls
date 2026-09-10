using Csls.Debugger.Contracts;

namespace Csls.Debugger.Control;

/// <summary>
/// Invokes managed-IL breakpoint operations on debugger control RPC.
/// </summary>
public sealed partial class DebuggerRpcClient
{
    /// <summary>
    /// Replaces every managed-IL instruction breakpoint.
    /// </summary>
    /// <param name="request">The complete replacement breakpoint set.</param>
    /// <param name="cancellationToken">Cancels breakpoint binding.</param>
    /// <returns>The ordered current breakpoint states.</returns>
    public Task<IReadOnlyList<DebugInstructionBreakpointInfo>>
        SetInstructionBreakpointsAsync(
            DebugInstructionBreakpointSetRequest request,
            CancellationToken cancellationToken) =>
        InvokeAsync<DebugInstructionBreakpointSetRequest,
            IReadOnlyList<DebugInstructionBreakpointInfo>>(
                DebuggerControlMethods.SetInstructionBreakpoints,
                request,
                cancellationToken);
}
