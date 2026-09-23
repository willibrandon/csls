namespace Csls.Debugger.Contracts;

/// <summary>
/// Defines read-only breakpoint inspection exposed through private debugger control RPC.
/// </summary>
public partial interface IDebuggerControlTarget
{
    /// <summary>
    /// Gets every configured breakpoint and managed-exception policy.
    /// </summary>
    /// <param name="cancellationToken">Cancels breakpoint inspection.</param>
    /// <returns>The authoritative ordered breakpoint snapshot.</returns>
    Task<DebugBreakpointSnapshot> GetBreakpointsAsync(CancellationToken cancellationToken);
}
