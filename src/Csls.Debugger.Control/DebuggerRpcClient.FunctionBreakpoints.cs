using Csls.Debugger.Contracts;

namespace Csls.Debugger.Control;

/// <summary>
/// Invokes managed function-breakpoint operations on debugger control RPC.
/// </summary>
public sealed partial class DebuggerRpcClient
{
    /// <summary>
    /// Replaces every managed function breakpoint.
    /// </summary>
    /// <param name="request">The complete replacement breakpoint set.</param>
    /// <param name="cancellationToken">Cancels breakpoint binding.</param>
    /// <returns>The ordered current breakpoint states.</returns>
    public Task<IReadOnlyList<DebugFunctionBreakpointInfo>> SetFunctionBreakpointsAsync(
        DebugFunctionBreakpointSetRequest request,
        CancellationToken cancellationToken) =>
        InvokeAsync<DebugFunctionBreakpointSetRequest,
            IReadOnlyList<DebugFunctionBreakpointInfo>>(
                DebuggerControlMethods.SetFunctionBreakpoints,
                request,
                cancellationToken);
}
