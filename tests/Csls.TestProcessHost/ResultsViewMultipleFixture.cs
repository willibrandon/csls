namespace Csls.TestProcessHost;

/// <summary>
/// Declares a generic enumerable that takes precedence over a different inherited one.
/// </summary>
internal sealed class ResultsViewMultipleFixture : ResultsViewFixture<string>, IEnumerable<int>
{
    private static readonly int[] s_items = [91, 92];

    /// <summary>
    /// Creates distinct values for the inherited enumerable implementation.
    /// </summary>
    internal ResultsViewMultipleFixture() : base(["incorrect inherited interface"])
    {
    }

    /// <summary>
    /// Enumerates the values declared by the most-derived generic interface.
    /// </summary>
    /// <returns>The integer values expected from Results View selection.</returns>
    IEnumerator<int> IEnumerable<int>.GetEnumerator()
    {
        _enumerationCount++;
        return ((IEnumerable<int>)s_items).GetEnumerator();
    }
}
