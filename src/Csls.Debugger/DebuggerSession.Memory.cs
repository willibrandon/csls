using Csls.Debugger.Contracts;

namespace Csls.Debugger;

/// <summary>
/// Exposes generation-bound target-memory inspection.
/// </summary>
public sealed partial class DebuggerSession
{
    /// <summary>
    /// Reads a bounded memory range relative to an opaque stopped-state handle.
    /// </summary>
    /// <param name="memoryReference">The generation-bound memory handle.</param>
    /// <param name="offset">The signed byte offset from the handle.</param>
    /// <param name="count">The requested byte count.</param>
    /// <param name="cancellationToken">Cancels queueing memory inspection.</param>
    /// <returns>The readable prefix and trailing unreadable byte count.</returns>
    public async Task<DebugMemoryReadResult> ReadMemoryAsync(
        string memoryReference,
        long offset,
        int count,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        DebugMemoryReadResult? result = null;
        await _actor.InvokeAsync(
            token =>
            {
                _ = token;
                if (_state != DebugSessionState.Stopped ||
                    _debuggee is not CorDebugDebuggee managedDebuggee)
                {
                    throw new InvalidOperationException(
                        $"Managed memory is unavailable while the debugger session is {_state}.");
                }

                result = managedDebuggee.ReadMemory(
                    memoryReference,
                    _stopGeneration,
                    offset,
                    count);
                return ValueTask.CompletedTask;
            },
            cancellationToken).ConfigureAwait(false);
        return result!;
    }
}
