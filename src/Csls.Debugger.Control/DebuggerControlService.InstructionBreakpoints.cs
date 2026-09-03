using Csls.Debugger.Contracts;

namespace Csls.Debugger.Control;

/// <summary>
/// Exposes managed-IL breakpoint operations through debugger control RPC.
/// </summary>
public sealed partial class DebuggerControlService
{
    /// <inheritdoc />
    public Task<IReadOnlyList<DebugInstructionBreakpointInfo>>
        SetInstructionBreakpointsAsync(
            DebugInstructionBreakpointSetRequest request,
            CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return _session.SetInstructionBreakpointsAsync(request.Breakpoints, cancellationToken);
    }

    /// <inheritdoc />
    public ValueTask OnInstructionBreakpointChangedAsync(
        DebugInstructionBreakpointInfo breakpoint,
        CancellationToken cancellationToken)
    {
        _ = breakpoint;
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.CompletedTask;
    }
}
