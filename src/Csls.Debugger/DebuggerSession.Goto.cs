using Csls.Debugger.Contracts;

namespace Csls.Debugger;

/// <summary>
/// Exposes safe managed instruction-pointer movement.
/// </summary>
public sealed partial class DebuggerSession
{
    /// <summary>
    /// Gets CoreCLR-approved destinations for an active managed frame.
    /// </summary>
    /// <param name="request">The selected frame and source position.</param>
    /// <param name="cancellationToken">Cancels queueing destination discovery.</param>
    /// <returns>The ordered safe destinations.</returns>
    public async Task<IReadOnlyList<DebugGotoTargetInfo>> GetGotoTargetsAsync(
        DebugGotoTargetsRequest request,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        IReadOnlyList<DebugGotoTargetInfo>? result = null;
        await _actor.InvokeAsync(
            token =>
            {
                _ = token;
                EnsureStoppedForGoto();
                result = ((CorDebugDebuggee)_debuggee!).GetGotoTargets(
                    request,
                    _stopGeneration);
                return ValueTask.CompletedTask;
            },
            cancellationToken).ConfigureAwait(false);
        return result!;
    }

    /// <summary>
    /// Moves a managed thread to a previously approved source destination.
    /// </summary>
    /// <param name="request">The selected thread and destination.</param>
    /// <param name="cancellationToken">Cancels queueing instruction-pointer movement.</param>
    /// <returns>A task that completes after the new stopped generation is published.</returns>
    public Task GotoAsync(
        DebugGotoRequest request,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        return _actor.InvokeAsync(
            async token =>
            {
                EnsureStoppedForGoto();
                ((CorDebugDebuggee)_debuggee!).SetInstructionPointer(
                    request,
                    _stopGeneration);
                await EnterStoppedStateAsync("goto", request.ThreadId, token)
                    .ConfigureAwait(false);
            },
            cancellationToken);
    }

    private void EnsureStoppedForGoto()
    {
        if (_state != DebugSessionState.Stopped || _debuggee is not CorDebugDebuggee)
        {
            throw new InvalidOperationException(
                $"Instruction-pointer movement is unavailable while the debugger session is {_state}.");
        }
    }
}
