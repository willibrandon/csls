namespace Csls.Debugger.Contracts;

/// <summary>
/// Defines module inspection exposed through private debugger control RPC.
/// </summary>
public partial interface IDebuggerControlTarget
{
    /// <summary>
    /// Gets a bounded page of modules in the active target.
    /// </summary>
    /// <param name="request">The selected module page.</param>
    /// <param name="cancellationToken">Cancels module enumeration.</param>
    /// <returns>The requested module page and complete count.</returns>
    Task<DebugModulePage> GetModulesAsync(
        DebugModulesRequest request,
        CancellationToken cancellationToken);
}
