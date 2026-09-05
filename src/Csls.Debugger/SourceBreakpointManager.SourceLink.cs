using Csls.Debugger.Contracts;

namespace Csls.Debugger;

/// <summary>
/// Retrieves and validates Source Link content for the source catalog.
/// </summary>
internal sealed partial class SourceBreakpointManager
{
    /// <summary>
    /// Gets embedded or Source Link content by its positive session-local reference.
    /// </summary>
    /// <param name="sourceReference">The reference returned in a source descriptor.</param>
    /// <param name="cancellationToken">Cancels Source Link retrieval.</param>
    /// <returns>The complete source text and media type.</returns>
    internal async Task<DebugSourceContent> GetSourceContentAsync(
        int sourceReference,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sourceReference);
        if (!_sourcesByReference.TryGetValue(
            sourceReference,
            out DebugSourceRegistration? registration))
        {
            throw new KeyNotFoundException(
                $"Source reference {sourceReference} does not exist in this debugger session.");
        }

        if (registration.Content is not null)
        {
            return registration.Content;
        }

        if (registration.SourceLinkUri is null || registration.Info.Checksum is null)
        {
            throw new KeyNotFoundException(
                $"Source reference {sourceReference} has no retrievable content.");
        }

        byte[] source = await SourceLinkSourceDownloader.DownloadAsync(
            registration.SourceLinkUri,
            _sourceLinkPolicy,
            cancellationToken).ConfigureAwait(false);
        if (!SourceChecksumVerifier.Matches(source, registration.Info.Checksum))
        {
            throw new InvalidDataException(
                "Source Link content does not match its Portable PDB checksum.");
        }

        registration.Content = new DebugSourceContent(
            SourceTextDecoder.Decode(source),
            GetMimeType(registration.Info.Name));
        return registration.Content;
    }
}
