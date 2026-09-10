using Csls.Debugger.Contracts;

namespace Csls.Debugger.Control;

/// <summary>
/// Invokes generation-safe managed Hot Reload operations.
/// </summary>
public sealed partial class DebuggerRpcClient
{
    /// <summary>
    /// Applies one compiler-produced update to an enabled managed module.
    /// </summary>
    /// <param name="request">The exact stopped and module generation with compiler deltas.</param>
    /// <param name="cancellationToken">Cancels validation before target mutation begins.</param>
    /// <returns>The committed module generation and replacement stopped generation.</returns>
    public Task<DebugHotReloadResult> ApplyHotReloadAsync(
        DebugHotReloadRequest request,
        CancellationToken cancellationToken) =>
        InvokeAsync<DebugHotReloadRequest, DebugHotReloadResult>(
            DebuggerControlMethods.ApplyHotReload,
            request,
            cancellationToken);
}
