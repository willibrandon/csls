using Csls.Debugger.Contracts;

namespace Csls.Debugger.Control;

/// <summary>
/// Invokes read-only breakpoint inspection through private debugger control RPC.
/// </summary>
public sealed partial class DebuggerRpcClient
{
    /// <summary>
    /// Gets every configured breakpoint and managed-exception policy.
    /// </summary>
    /// <param name="cancellationToken">Cancels breakpoint inspection.</param>
    /// <returns>The authoritative ordered breakpoint snapshot.</returns>
    public Task<DebugBreakpointSnapshot> GetBreakpointsAsync(
        CancellationToken cancellationToken) =>
        GetRpc().InvokeWithCancellationAsync<DebugBreakpointSnapshot>(
            DebuggerControlMethods.GetBreakpoints,
            cancellationToken: cancellationToken);
}
