using Csls.Debugger.Contracts;

namespace Csls.Debugger.Control;

/// <summary>
/// Exposes managed-IL disassembly through private control RPC.
/// </summary>
public sealed partial class DebuggerControlService
{
    /// <inheritdoc />
    public Task<DebugDisassembly> DisassembleAsync(
        DebugDisassemblyRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return _session.DisassembleAsync(request, cancellationToken);
    }
}
