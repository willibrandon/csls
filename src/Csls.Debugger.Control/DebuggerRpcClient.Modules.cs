using Csls.Debugger.Contracts;

namespace Csls.Debugger.Control;

/// <summary>
/// Invokes managed module inspection through debugger control RPC.
/// </summary>
public sealed partial class DebuggerRpcClient
{
    /// <summary>
    /// Gets a bounded page of managed modules.
    /// </summary>
    /// <param name="request">The selected module page.</param>
    /// <param name="cancellationToken">Cancels module enumeration.</param>
    /// <returns>The requested module page and complete count.</returns>
    public Task<DebugModulePage> GetModulesAsync(
        DebugModulesRequest request,
        CancellationToken cancellationToken) =>
        InvokeAsync<DebugModulesRequest, DebugModulePage>(
            DebuggerControlMethods.GetModules,
            request,
            cancellationToken);
}
