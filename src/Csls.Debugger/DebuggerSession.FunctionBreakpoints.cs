using Csls.Debugger.Contracts;

namespace Csls.Debugger;

/// <summary>
/// Exposes managed function-breakpoint configuration operations.
/// </summary>
public sealed partial class DebuggerSession
{
    /// <summary>
    /// Replaces every managed function breakpoint in the session.
    /// </summary>
    /// <param name="breakpoints">The complete replacement breakpoint list.</param>
    /// <param name="cancellationToken">Cancels queueing or runtime binding.</param>
    /// <returns>The ordered current breakpoint binding states.</returns>
    public async Task<IReadOnlyList<DebugFunctionBreakpointInfo>> SetFunctionBreakpointsAsync(
        IReadOnlyList<DebugFunctionBreakpointRequest> breakpoints,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        IReadOnlyList<DebugFunctionBreakpointInfo>? result = null;
        await _actor.InvokeAsync(
            async token =>
            {
                if (_state is not DebugSessionState.Created and not DebugSessionState.Stopped)
                {
                    throw new InvalidOperationException(
                        $"Function breakpoints cannot be changed while the debugger session is {_state}.");
                }

                result = await _functionBreakpoints.SetAsync(breakpoints, token)
                    .ConfigureAwait(false);
            },
            cancellationToken).ConfigureAwait(false);
        return result!;
    }
}
