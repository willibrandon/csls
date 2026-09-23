namespace Csls.TestProcessHost;

/// <summary>
/// Wraps a real asynchronous sequence with source-visible consumer plumbing.
/// </summary>
/// <param name="source">The sequence consumed by the debugger fixture.</param>
internal sealed class DebuggerAsyncEnumerable(IAsyncEnumerable<int> source) : IAsyncEnumerable<int>
{
    /// <summary>
    /// Creates the source-visible enumerator used by an await-foreach loop.
    /// </summary>
    public IAsyncEnumerator<int> GetAsyncEnumerator(CancellationToken cancellationToken = default) =>
        new DebuggerAsyncEnumerator(source.GetAsyncEnumerator(cancellationToken));
}
