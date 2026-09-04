namespace Csls.TestProcessHost;

/// <summary>
/// Projects an enumerable as a named value without invoking its enumerator.
/// </summary>
internal sealed class ResultsViewFixtureProxy
{
    /// <summary>
    /// Retains a projected value that differs from the enumerable contents.
    /// </summary>
    public readonly int Value = 112;

    /// <summary>
    /// Creates the debugger presentation without enumerating the supplied target.
    /// </summary>
    /// <param name="target">The enumerable represented by the debugger proxy.</param>
    public ResultsViewFixtureProxy(ResultsViewProxiedFixture target)
    {
        ArgumentNullException.ThrowIfNull(target);
    }
}
