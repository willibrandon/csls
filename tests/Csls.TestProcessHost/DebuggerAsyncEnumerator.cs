namespace Csls.TestProcessHost;

/// <summary>
/// Keeps ordinary await-foreach adapter methods visible to source stepping.
/// </summary>
/// <param name="source">The actual asynchronous enumerator.</param>
internal sealed class DebuggerAsyncEnumerator(IAsyncEnumerator<int> source) : IAsyncEnumerator<int>
{
    /// <summary>
    /// Gets the current value from the real iterator.
    /// </summary>
    public int Current => source.Current;

    /// <summary>
    /// Requests the next value through an authored synchronous wrapper.
    /// </summary>
    public ValueTask<bool> MoveNextAsync()
    {
        ValueTask<bool> pending = source.MoveNextAsync();
        return pending;
    }

    /// <summary>
    /// Releases the underlying iterator through its asynchronous disposal contract.
    /// </summary>
    public ValueTask DisposeAsync() => source.DisposeAsync();
}
