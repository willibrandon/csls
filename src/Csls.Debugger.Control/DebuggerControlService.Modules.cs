using Csls.Debugger.Contracts;

namespace Csls.Debugger.Control;

/// <summary>
/// Exposes managed module inspection through debugger control RPC.
/// </summary>
public sealed partial class DebuggerControlService
{
    /// <inheritdoc />
    public Task<DebugModulePage> GetModulesAsync(
        DebugModulesRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return _session.GetModulesAsync(
            request.StartModule,
            request.ModuleCount,
            cancellationToken);
    }
}
