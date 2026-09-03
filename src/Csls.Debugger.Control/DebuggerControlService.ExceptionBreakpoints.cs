using Csls.Debugger.Contracts;

namespace Csls.Debugger.Control;

/// <summary>
/// Exposes managed exception-breakpoint configuration through private control RPC.
/// </summary>
public sealed partial class DebuggerControlService
{
    /// <inheritdoc />
    public Task SetExceptionBreakpointsAsync(
        DebugExceptionBreakpointSetRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return _session.SetExceptionBreakpointsAsync(request, cancellationToken);
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
