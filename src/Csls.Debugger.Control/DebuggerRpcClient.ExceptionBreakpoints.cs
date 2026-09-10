using Csls.Debugger.Contracts;

namespace Csls.Debugger.Control;

/// <summary>
/// Invokes managed exception-breakpoint operations on debugger control RPC.
/// </summary>
public sealed partial class DebuggerRpcClient
{
    /// <summary>
    /// Replaces the complete managed exception breakpoint policy.
    /// </summary>
    /// <param name="request">The complete replacement policy.</param>
    /// <param name="cancellationToken">Cancels exception policy configuration.</param>
    /// <returns>A task that completes after the policy is applied.</returns>
    public async Task SetExceptionBreakpointsAsync(
        DebugExceptionBreakpointSetRequest request,
        CancellationToken cancellationToken)
    {
        _ = await InvokeAsync<DebugExceptionBreakpointSetRequest, object?>(
            DebuggerControlMethods.SetExceptionBreakpoints,
            request,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Gets the managed exception responsible for the current stop.
    /// </summary>
    /// <param name="request">The selected managed thread.</param>
    /// <param name="cancellationToken">Cancels exception inspection.</param>
    /// <returns>The current managed exception details.</returns>
    public Task<DebugExceptionInfo> GetExceptionInfoAsync(
        DebugExceptionInfoRequest request,
        CancellationToken cancellationToken) =>
        InvokeAsync<DebugExceptionInfoRequest, DebugExceptionInfo>(
            DebuggerControlMethods.GetExceptionInfo,
            request,
            cancellationToken);
}
