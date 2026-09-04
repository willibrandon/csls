namespace Csls.TestProcessHost;

/// <summary>
/// Keeps the proxy type inheritable so protected-member presentation is exercised.
/// </summary>
internal sealed class DebuggerTypeProxyFixtureProxyDerived : DebuggerTypeProxyFixtureProxy
{
    /// <summary>
    /// Creates the concrete proxy subtype used by the debugger fixture.
    /// </summary>
    /// <param name="target">The original runtime object.</param>
    internal DebuggerTypeProxyFixtureProxyDerived(DebuggerTypeProxyFixture target)
        : base(target)
    {
    }
}
