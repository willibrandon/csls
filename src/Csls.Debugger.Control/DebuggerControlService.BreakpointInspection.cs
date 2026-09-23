using Csls.Debugger.Contracts;

namespace Csls.Debugger.Control;

/// <summary>
/// Exposes authoritative breakpoint state through private debugger control RPC.
/// </summary>
public sealed partial class DebuggerControlService
{
    /// <inheritdoc />
    public Task<DebugBreakpointSnapshot> GetBreakpointsAsync(
        CancellationToken cancellationToken) =>
        _session.GetBreakpointsAsync(cancellationToken);
}
