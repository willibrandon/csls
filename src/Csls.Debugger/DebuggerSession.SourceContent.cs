using Csls.Debugger.Contracts;

namespace Csls.Debugger;

/// <summary>
/// Exposes session-local debugger source content.
/// </summary>
public sealed partial class DebuggerSession
{
    /// <summary>
    /// Replaces build-time to local source mappings before target activation.
    /// </summary>
    /// <param name="mappings">The complete source mapping dictionary.</param>
    /// <param name="sourceLinkOptions">The complete Source Link URL policy.</param>
    /// <param name="cancellationToken">Cancels queueing the configuration.</param>
    /// <returns>A task that completes after mappings are validated and installed.</returns>
    public Task ConfigureSourceOptionsAsync(
        IReadOnlyDictionary<string, string> mappings,
        IReadOnlyDictionary<string, bool> sourceLinkOptions,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        ArgumentNullException.ThrowIfNull(mappings);
        ArgumentNullException.ThrowIfNull(sourceLinkOptions);
        return _actor.InvokeAsync(
            token =>
            {
                _ = token;
                if (_state != DebugSessionState.Created)
                {
                    throw new InvalidOperationException(
                        $"Source mappings cannot be changed while the debugger session is {_state}.");
                }

                _sourceBreakpoints.SetSourceOptions(mappings, sourceLinkOptions);
                return ValueTask.CompletedTask;
            },
            cancellationToken);
    }

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
