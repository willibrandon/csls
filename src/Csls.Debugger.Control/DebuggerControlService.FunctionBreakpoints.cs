using Csls.Debugger.Contracts;

namespace Csls.Debugger.Control;

/// <summary>
/// Exposes managed function-breakpoint operations through debugger control RPC.
/// </summary>
public sealed partial class DebuggerControlService
{
    /// <inheritdoc />
    public async Task<IReadOnlyList<DebugFunctionBreakpointInfo>> SetFunctionBreakpointsAsync(
        DebugFunctionBreakpointSetRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        IReadOnlyList<DebugFunctionBreakpointInfo> result =
            await _session.SetFunctionBreakpointsAsync(
                request.Breakpoints,
                cancellationToken).ConfigureAwait(false);
        NotifyResourceChanged(DebuggerResourceChangeKind.Breakpoints);
        return result;
    }

    /// <inheritdoc />
    public ValueTask OnFunctionBreakpointChangedAsync(
        DebugFunctionBreakpointInfo breakpoint,
        CancellationToken cancellationToken)
    {
        _ = breakpoint;
        cancellationToken.ThrowIfCancellationRequested();
        NotifyResourceChanged(DebuggerResourceChangeKind.Breakpoints);
        return ValueTask.CompletedTask;
    }
}
