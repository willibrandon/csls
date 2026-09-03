using Csls.Debugger.Contracts;

namespace Csls.Debugger.Control;

/// <summary>
/// Exposes managed-IL breakpoint operations through debugger control RPC.
/// </summary>
public sealed partial class DebuggerControlService
{
    /// <inheritdoc />
    public async Task<IReadOnlyList<DebugInstructionBreakpointInfo>>
        SetInstructionBreakpointsAsync(
            DebugInstructionBreakpointSetRequest request,
            CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        IReadOnlyList<DebugInstructionBreakpointInfo> result =
            await _session.SetInstructionBreakpointsAsync(
                request.Breakpoints,
                cancellationToken).ConfigureAwait(false);
        NotifyResourceChanged(DebuggerResourceChangeKind.Breakpoints);
        return result;
    }

    /// <inheritdoc />
    public ValueTask OnInstructionBreakpointChangedAsync(
        DebugInstructionBreakpointInfo breakpoint,
        CancellationToken cancellationToken)
    {
        _ = breakpoint;
        cancellationToken.ThrowIfCancellationRequested();
        NotifyResourceChanged(DebuggerResourceChangeKind.Breakpoints);
        return ValueTask.CompletedTask;
    }
}
