using Csls.Debugger.Contracts;

namespace Csls.Debugger.Control;

/// <summary>
/// Invokes managed-IL disassembly over private control RPC.
/// </summary>
public sealed partial class DebuggerRpcClient
{
    /// <summary>
    /// Disassembles a bounded managed-IL range through a stopped-state handle.
    /// </summary>
    /// <param name="request">The selected IL handle and instruction window.</param>
    /// <param name="cancellationToken">Cancels disassembly.</param>
    /// <returns>The exact-count managed-IL response.</returns>
    public Task<DebugDisassembly> DisassembleAsync(
        DebugDisassemblyRequest request,
        CancellationToken cancellationToken) =>
        InvokeAsync<DebugDisassemblyRequest, DebugDisassembly>(
            DebuggerControlMethods.Disassemble,
            request,
            cancellationToken);
}
