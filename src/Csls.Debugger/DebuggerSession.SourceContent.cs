using Csls.Debugger.Contracts;

namespace Csls.Debugger;

/// <summary>
/// Exposes session-local debugger source content.
/// </summary>
public sealed partial class DebuggerSession
{
    /// <summary>
    /// Gets source text previously identified by a debugger source descriptor.
    /// </summary>
    /// <param name="sourceReference">The positive session-local source reference.</param>
    /// <param name="cancellationToken">Cancels queueing source retrieval.</param>
    /// <returns>The complete source text and media type.</returns>
    public async Task<DebugSourceContent> GetSourceContentAsync(
        int sourceReference,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        DebugSourceContent? result = null;
        await _actor.InvokeAsync(
            async token =>
            {
                EnsureSymbolsAvailable();
                result = await _sourceBreakpoints.GetSourceContentAsync(
                    sourceReference,
                    token).ConfigureAwait(false);
            },
            cancellationToken).ConfigureAwait(false);
        return result!;
    }
}
