using Csls.Debugger.Contracts;

namespace Csls.Debugger.Control;

/// <summary>
/// Exposes bounded debugger memory reads through private control RPC.
/// </summary>
public sealed partial class DebuggerControlService
{
    /// <inheritdoc />
    public Task<DebugMemoryReadResult> ReadMemoryAsync(
        DebugMemoryReadRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return _session.ReadMemoryAsync(
            request.MemoryReference,
            request.Offset,
            request.Count,
            cancellationToken);
    }
}
