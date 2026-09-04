namespace Csls.TestProcessHost;

/// <summary>
/// Projects a closed generic target using its exact substituted runtime argument.
/// </summary>
/// <typeparam name="T">The substituted target type argument.</typeparam>
internal sealed class GenericDebuggerTypeProxyFixtureProxy<T>
{
    /// <summary>
    /// Creates the generic debugger projection.
    /// </summary>
    /// <param name="target">The original closed generic target.</param>
    private GenericDebuggerTypeProxyFixtureProxy(GenericDebuggerTypeProxyFixture<T> target) =>
        Value = target._rawValue;

    /// <summary>
    /// Gets the projected generic value.
    /// </summary>
    public readonly T Value;
}
