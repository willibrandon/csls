using System.Collections;

namespace Csls.TestProcessHost;

/// <summary>
/// Exposes two distinct constructions of one generic inference interface.
/// </summary>
internal sealed class DebuggerAmbiguousEnumerable : IEnumerable<int>, IEnumerable<string>
{
    IEnumerator<int> IEnumerable<int>.GetEnumerator() => Enumerable.Repeat(1, 1).GetEnumerator();

    IEnumerator<string> IEnumerable<string>.GetEnumerator() => Enumerable.Repeat("one", 1).GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => ((IEnumerable<int>)this).GetEnumerator();
}
