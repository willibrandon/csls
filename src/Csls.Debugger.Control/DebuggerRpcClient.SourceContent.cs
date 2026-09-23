using Csls.Debugger.Contracts;

namespace Csls.Debugger.Control;

/// <summary>
/// Retrieves session-local debugger source content through control RPC.
/// </summary>
public sealed partial class DebuggerRpcClient
{
    /// <summary>
    /// Gets source text by its positive session-local reference.
    /// </summary>
    /// <param name="request">The selected source reference.</param>
    /// <param name="cancellationToken">Cancels source retrieval.</param>
    /// <returns>The complete source text and media type.</returns>
    public Task<DebugSourceContent> GetSourceContentAsync(
        DebugSourceRequest request,
        CancellationToken cancellationToken) =>
        InvokeAsync<DebugSourceRequest, DebugSourceContent>(
            DebuggerControlMethods.GetSourceContent,
            request,
            cancellationToken);
}
