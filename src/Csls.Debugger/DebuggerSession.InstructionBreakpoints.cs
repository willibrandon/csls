using Csls.Debugger.Contracts;

namespace Csls.Debugger;

/// <summary>
/// Exposes generation-safe managed-IL breakpoint configuration.
/// </summary>
public sealed partial class DebuggerSession
{
    /// <summary>
    /// Replaces every managed-IL instruction breakpoint in the session.
    /// </summary>
    /// <param name="breakpoints">The complete replacement breakpoint list.</param>
    /// <param name="cancellationToken">Cancels queueing or runtime binding.</param>
    /// <returns>The ordered current breakpoint binding states.</returns>
    public async Task<IReadOnlyList<DebugInstructionBreakpointInfo>>
        SetInstructionBreakpointsAsync(
            IReadOnlyList<DebugInstructionBreakpointRequest> breakpoints,
            CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        ArgumentNullException.ThrowIfNull(breakpoints);
        IReadOnlyList<DebugInstructionBreakpointInfo>? result = null;
        await _actor.InvokeAsync(
            async token =>
            {
                if (breakpoints.Count == 0 && _state == DebugSessionState.Created)
                {
                    result = await _instructionBreakpoints.SetAsync([], token)
                        .ConfigureAwait(false);
                    return;
                }

                if (_state != DebugSessionState.Stopped ||
                    _debuggee is not CorDebugDebuggee managedDebuggee)
                {
                    throw new InvalidOperationException(
                        $"Managed-IL breakpoints cannot be changed while the debugger session is {_state}.");
                }

                IReadOnlyList<ManagedInstructionBreakpointRequest> resolved = managedDebuggee
                    .ResolveInstructionBreakpoints(breakpoints, _stopGeneration);
                result = await _instructionBreakpoints.SetAsync(resolved, token)
                    .ConfigureAwait(false);
            },
            cancellationToken).ConfigureAwait(false);
        return result!;
    }
}
