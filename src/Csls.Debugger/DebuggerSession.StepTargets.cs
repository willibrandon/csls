using Csls.Debugger.Contracts;

namespace Csls.Debugger;

/// <summary>
/// Exposes generation-bound source-aware Step Into targets.
/// </summary>
public sealed partial class DebuggerSession
{
    /// <summary>
    /// Gets selectable managed calls in a current frame's source statement.
    /// </summary>
    /// <param name="frameId">The generation-bound active frame identifier.</param>
    /// <param name="cancellationToken">Cancels queueing target discovery.</param>
    /// <returns>The ordered source-aware Step Into targets.</returns>
    public async Task<IReadOnlyList<DebugStepTargetInfo>> GetStepTargetsAsync(
        int frameId,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        IReadOnlyList<DebugStepTargetInfo>? result = null;
        await _actor.InvokeAsync(
            token =>
            {
                _ = token;
                if (_state != DebugSessionState.Stopped ||
                    _debuggee is not CorDebugDebuggee managedDebuggee)
                {
                    throw new InvalidOperationException(
                        $"Step Into targets are unavailable while the debugger session is {_state}.");
                }

                result = managedDebuggee.GetStepTargets(frameId, _stopGeneration);
                return ValueTask.CompletedTask;
            },
            cancellationToken).ConfigureAwait(false);
        return result!;
    }
}
