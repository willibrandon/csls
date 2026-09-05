using System.Collections;

namespace Csls.TestProcessHost;

/// <summary>
/// Provides a collection that supports only the non-generic enumerable contract.
/// </summary>
internal sealed class ResultsViewNonGenericFixture : IEnumerable
{
    private static readonly int[] s_items = [81, 82];

    /// <summary>
    /// Records observable execution of the non-generic enumerator.
    /// </summary>
    internal int _enumerationCount;

    /// <summary>
    /// Enumerates boxed values and records that target code ran.
    /// </summary>
    /// <returns>The ordered values exposed by the non-generic Results View.</returns>
    public IEnumerator GetEnumerator()
    {
        _enumerationCount++;
        return s_items.GetEnumerator();
    }
}
