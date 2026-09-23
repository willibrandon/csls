using Csls.Debugger.Contracts;

namespace Csls.Debugger.Control;

/// <summary>
/// Invokes bounded debugger memory inspection over private control RPC.
/// </summary>
public sealed partial class DebuggerRpcClient
{
    /// <summary>
    /// Reads a bounded target-memory range through a stopped-state handle.
    /// </summary>
    /// <param name="request">The selected memory handle and relative range.</param>
    /// <param name="cancellationToken">Cancels memory inspection.</param>
    /// <returns>The readable prefix and trailing unreadable byte count.</returns>
    public Task<DebugMemoryReadResult> ReadMemoryAsync(
        DebugMemoryReadRequest request,
        CancellationToken cancellationToken) =>
        InvokeAsync<DebugMemoryReadRequest, DebugMemoryReadResult>(
            DebuggerControlMethods.ReadMemory,
            request,
            cancellationToken);
}
