namespace Csls.Debugger;

/// <summary>
/// Makes asynchronous standard-stream ownership transfer explicit during launch.
/// </summary>
internal sealed class DbgShimStandardStreamsOwner : IAsyncDisposable
{
    private DbgShimStandardStreams? _streams = new();

    /// <summary>
    /// Gets the currently owned stream bundle.
    /// </summary>
    internal DbgShimStandardStreams Value => _streams ?? throw new InvalidOperationException(
        "The dbgshim standard streams have already transferred ownership.");

    /// <summary>
    /// Transfers the stream bundle to the successfully constructed debuggee.
    /// </summary>
    /// <returns>The detached stream bundle.</returns>
    internal DbgShimStandardStreams Detach() => Interlocked.Exchange(ref _streams, null)
        ?? throw new InvalidOperationException(
            "The dbgshim standard streams have already transferred ownership.");

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_streams is not null)
        {
            await _streams.DisposeAsync().ConfigureAwait(false);
            _streams = null;
        }
    }
}
