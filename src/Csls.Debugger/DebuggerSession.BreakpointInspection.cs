using Csls.Debugger.Contracts;

namespace Csls.Debugger;

/// <summary>
/// Exposes authoritative read-only breakpoint state.
/// </summary>
public sealed partial class DebuggerSession
{
    /// <summary>
    /// Gets every breakpoint and managed-exception policy in this session.
    /// </summary>
    /// <param name="cancellationToken">Cancels queueing the inspection.</param>
    /// <returns>The current ordered breakpoint snapshot.</returns>
    public async Task<DebugBreakpointSnapshot> GetBreakpointsAsync(
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        DebugBreakpointSnapshot? result = null;
        await _actor.InvokeAsync(
            token =>
            {
                _ = token;
                result = new DebugBreakpointSnapshot(
                    _sourceBreakpoints.GetBreakpoints(),
                    _functionBreakpoints.GetBreakpoints(),
                    _instructionBreakpoints.GetBreakpoints(),
                    _exceptionBreakpoints.Select(static breakpoint => breakpoint.ToRequest())
                        .ToArray());
                return ValueTask.CompletedTask;
            },
            cancellationToken).ConfigureAwait(false);
        return result!;
    }
}
