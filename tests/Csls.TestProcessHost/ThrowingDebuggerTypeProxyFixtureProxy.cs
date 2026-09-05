namespace Csls.TestProcessHost;

/// <summary>
/// Throws during construction to exercise debugger proxy failure isolation.
/// </summary>
internal sealed class ThrowingDebuggerTypeProxyFixtureProxy
{
    /// <summary>
    /// Rejects construction after receiving the original target.
    /// </summary>
    /// <param name="target">The original debugger target.</param>
    private ThrowingDebuggerTypeProxyFixtureProxy(ThrowingDebuggerTypeProxyFixture target)
    {
        _ = target;
        throw new InvalidOperationException("proxy construction failure");
    }
}
