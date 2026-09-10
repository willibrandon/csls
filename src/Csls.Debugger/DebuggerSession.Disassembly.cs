using Csls.Debugger.Contracts;

namespace Csls.Debugger;

/// <summary>
/// Exposes generation-bound managed-IL disassembly.
/// </summary>
public sealed partial class DebuggerSession
{
    /// <summary>
    /// Disassembles an exact-count managed-IL window around a stopped frame.
    /// </summary>
    /// <param name="request">The selected managed-IL location and window.</param>
    /// <param name="cancellationToken">Cancels queueing disassembly.</param>
    /// <returns>The requested instructions and out-of-range placeholders.</returns>
    public async Task<DebugDisassembly> DisassembleAsync(
        DebugDisassemblyRequest request,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        DebugDisassembly? result = null;
        await _actor.InvokeAsync(
            token =>
            {
                _ = token;
                if (_state != DebugSessionState.Stopped ||
                    _debuggee is not CorDebugDebuggee managedDebuggee)
                {
                    throw new InvalidOperationException(
                        $"Managed IL is unavailable while the debugger session is {_state}.");
                }

                result = managedDebuggee.Disassemble(request, _stopGeneration);
                return ValueTask.CompletedTask;
            },
            cancellationToken).ConfigureAwait(false);
        return result!;
    }
}
