namespace Csls.TestProcessHost;

/// <summary>
/// Provides an intentionally incompatible two-argument generic proxy.
/// </summary>
/// <typeparam name="T">The target value type.</typeparam>
/// <typeparam name="TUnused">An unmatched proxy type argument.</typeparam>
internal sealed class ArityMismatchDebuggerTypeProxyFixtureProxy<T, TUnused>
{
    /// <summary>
    /// Creates the incompatible projection when erroneously selected.
    /// </summary>
    /// <param name="target">The original target.</param>
    internal ArityMismatchDebuggerTypeProxyFixtureProxy(
        ArityMismatchDebuggerTypeProxyFixture<T> target) => Value = target.Value;

    /// <summary>
    /// Gets the projected value when erroneously constructed.
    /// </summary>
    public readonly T Value;
}
