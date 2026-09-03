using Csls.Debugger.Contracts;

namespace Csls.Debugger.Control;

/// <summary>
/// Exposes managed function-breakpoint operations through debugger control RPC.
/// </summary>
public sealed partial class DebuggerControlService
{
    /// <inheritdoc />
    public Task<IReadOnlyList<DebugFunctionBreakpointInfo>> SetFunctionBreakpointsAsync(
        DebugFunctionBreakpointSetRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return _session.SetFunctionBreakpointsAsync(request.Breakpoints, cancellationToken);
    }

    /// <inheritdoc />
    public ValueTask OnFunctionBreakpointChangedAsync(
        DebugFunctionBreakpointInfo breakpoint,
        CancellationToken cancellationToken)
    {
        _ = breakpoint;
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.CompletedTask;
    }
}
