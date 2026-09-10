using System.Collections;

namespace Csls.TestProcessHost;

/// <summary>
/// Provides a value-type enumerable with reference-owned observable execution state.
/// </summary>
internal readonly struct ResultsViewStructFixture : IEnumerable<int>
{
    /// <summary>
    /// Retains the shared enumeration counter across debugger-owned value copies.
    /// </summary>
    internal readonly ResultsViewFixture<int> _state;

    /// <summary>
    /// Creates a value whose boxed and unboxed copies enumerate the same counted data.
    /// </summary>
    public ResultsViewStructFixture() => _state = new ResultsViewFixture<int>([151, 152]);

    /// <summary>
    /// Enumerates the reference-owned contents of this value.
    /// </summary>
    /// <returns>The counted generic enumerator for the retained values.</returns>
    public IEnumerator<int> GetEnumerator() => _state.GetEnumerator();

    /// <summary>
    /// Preserves the same enumeration behavior when consumed without generic typing.
    /// </summary>
    /// <returns>The counted enumerator for the retained values.</returns>
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
