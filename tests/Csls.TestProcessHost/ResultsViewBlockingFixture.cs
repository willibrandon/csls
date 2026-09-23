using System.Collections;

namespace Csls.TestProcessHost;

/// <summary>
/// Holds enumeration in target code until the debugger cancels its function evaluation.
/// </summary>
internal sealed class ResultsViewBlockingFixture : IEnumerable<int>
{
    private readonly string _signalPath;

    /// <summary>
    /// Records how often the debugger has requested an enumerator.
    /// </summary>
    internal int _enumerationCount;

    /// <summary>
    /// Records cleanup of the active iterator when enumeration unwinds.
    /// </summary>
    internal int _disposeCount;

    /// <summary>
    /// Creates a blocking enumerable with a real cross-process readiness signal.
    /// </summary>
    /// <param name="signalPath">The file written when the iterator begins executing.</param>
    internal ResultsViewBlockingFixture(string signalPath) => _signalPath = signalPath;

    /// <summary>
    /// Starts a counted iterator that exposes cancellation cleanup.
    /// </summary>
    /// <returns>The iterator used by the target's generic enumeration contract.</returns>
    public IEnumerator<int> GetEnumerator()
    {
        _enumerationCount++;
        return Enumerate().GetEnumerator();
    }

    /// <summary>
    /// Routes non-generic enumeration through the same observable iterator.
    /// </summary>
    /// <returns>The iterator used by the target's non-generic enumeration contract.</returns>
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    private IEnumerable<int> Enumerate()
    {
        try
        {
            File.WriteAllText(_signalPath, "started");
            while (!File.Exists(_signalPath + ".release"))
            {
                Thread.SpinWait(10_000);
            }

            yield return 141;
        }
        finally
        {
            _disposeCount++;
        }
    }
}
