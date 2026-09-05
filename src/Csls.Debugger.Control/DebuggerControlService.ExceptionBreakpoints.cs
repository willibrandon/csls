using Csls.Debugger.Contracts;

namespace Csls.Debugger.Control;

/// <summary>
/// Exposes managed exception-breakpoint configuration through private control RPC.
/// </summary>
public sealed partial class DebuggerControlService
{
    /// <inheritdoc />
    public async Task SetExceptionBreakpointsAsync(
        DebugExceptionBreakpointSetRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        await _session.SetExceptionBreakpointsAsync(request, cancellationToken)
            .ConfigureAwait(false);
        NotifyResourceChanged(DebuggerResourceChangeKind.Breakpoints);
    }

    /// <inheritdoc />
    public Task<DebugExceptionInfo> GetExceptionInfoAsync(
        DebugExceptionInfoRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return _session.GetExceptionInfoAsync(request.ThreadId, cancellationToken);
    }
}
