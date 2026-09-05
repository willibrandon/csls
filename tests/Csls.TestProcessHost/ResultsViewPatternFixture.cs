namespace Csls.TestProcessHost;

/// <summary>
/// Supports the foreach pattern without implementing either enumerable interface.
/// </summary>
internal sealed class ResultsViewPatternFixture
{
    private readonly int[] _items = [121];

    /// <summary>
    /// Retains an ordinary field so the pattern-only object remains expandable.
    /// </summary>
    internal readonly int _value = 121;

    /// <summary>
    /// Exposes an enumeration-shaped method without an enumerable metadata contract.
    /// </summary>
    /// <returns>The values a compiler could enumerate through the foreach pattern.</returns>
    public IEnumerator<int> GetEnumerator() => ((IEnumerable<int>)_items).GetEnumerator();
}
