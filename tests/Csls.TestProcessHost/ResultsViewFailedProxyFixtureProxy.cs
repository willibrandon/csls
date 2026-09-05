namespace Csls.TestProcessHost;

/// <summary>
/// Fails construction without executing the enumerable it represents.
/// </summary>
internal sealed class ResultsViewFailedProxyFixtureProxy
{
    /// <summary>
    /// Reports a deterministic proxy construction error for an otherwise valid collection.
    /// </summary>
    /// <param name="target">The enumerable whose ordinary fields remain available.</param>
    private ResultsViewFailedProxyFixtureProxy(ResultsViewFailedProxyFixture target)
    {
        ArgumentNullException.ThrowIfNull(target);
        throw new InvalidOperationException("Results View proxy construction failed.");
    }
}
