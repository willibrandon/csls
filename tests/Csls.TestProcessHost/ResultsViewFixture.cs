using System.Collections;

namespace Csls.TestProcessHost;

/// <summary>
/// Provides an observable enumerable whose execution is controlled by the debugger.
/// </summary>
/// <typeparam name="T">The exact runtime element type.</typeparam>
internal class ResultsViewFixture<T> : IResultsViewEnumerable<T>
{
    private static readonly string[] s_nonGenericItems = ["incorrect non-generic interface"];
    private readonly T[] _items;
    private readonly bool _throws;

    /// <summary>
    /// Records how often the target has started enumerating the collection.
    /// </summary>
    internal int _enumerationCount;

    /// <summary>
    /// Creates a collection with deterministic contents and optional enumeration failure.
    /// </summary>
    /// <param name="items">The ordered values returned by generic enumeration.</param>
    /// <param name="throws">Whether beginning enumeration throws an exception.</param>
    internal ResultsViewFixture(T[] items, bool throws = false)
    {
        _items = items;
        _throws = throws;
    }

    /// <summary>
    /// Begins generic enumeration and records its observable target side effect.
    /// </summary>
    /// <returns>The enumerator for the retained values.</returns>
    public IEnumerator<T> GetEnumerator()
    {
        _enumerationCount++;
        if (_throws)
        {
            throw new InvalidOperationException("Results View fixture enumeration failed.");
        }

        return ((IEnumerable<T>)_items).GetEnumerator();
    }

    /// <summary>
    /// Returns distinguishable values if the debugger selects the non-generic interface.
    /// </summary>
    /// <returns>An enumerator that cannot be confused with the generic result.</returns>
    IEnumerator IEnumerable.GetEnumerator()
    {
        _enumerationCount++;
        return s_nonGenericItems.GetEnumerator();
    }
}
