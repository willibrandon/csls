namespace Csls.TestProcessHost;

/// <summary>
/// Projects a target through an explicitly constructed generic proxy.
/// </summary>
/// <typeparam name="T">The attribute-supplied exact runtime type.</typeparam>
internal sealed class ClosedGenericDebuggerTypeProxyFixtureProxy<T>
{
    /// <summary>
    /// Creates the closed generic debugger projection.
    /// </summary>
    /// <param name="target">The original runtime target.</param>
    internal ClosedGenericDebuggerTypeProxyFixtureProxy(
        ClosedGenericDebuggerTypeProxyFixture target) =>
        Value = (T)(object)target._rawValue;

    /// <summary>
    /// Gets the projected value with its attribute-supplied runtime type.
    /// </summary>
    public readonly T Value;
}
