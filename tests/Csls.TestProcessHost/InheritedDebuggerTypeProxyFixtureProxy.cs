namespace Csls.TestProcessHost;

/// <summary>
/// Projects values from an inherited debugger proxy target.
/// </summary>
internal sealed class InheritedDebuggerTypeProxyFixtureProxy
{
    /// <summary>
    /// Creates the inherited debugger projection.
    /// </summary>
    /// <param name="target">The attributed base target.</param>
    private InheritedDebuggerTypeProxyFixtureProxy(
        InheritedDebuggerTypeProxyBaseFixture target) => Value = target._baseValue;

    /// <summary>
    /// Gets the projected base value.
    /// </summary>
    public readonly int Value;
}
