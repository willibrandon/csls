namespace Csls.TestProcessHost;

/// <summary>
/// Projects a closed generic target using its exact substituted runtime argument.
/// </summary>
/// <typeparam name="T">The substituted target type argument.</typeparam>
internal sealed class GenericDebuggerTypeProxyFixtureProxy<T>
{
    private readonly T _value;

    /// <summary>
    /// Creates the generic debugger projection.
    /// </summary>
    /// <param name="target">The original closed generic target.</param>
    private GenericDebuggerTypeProxyFixtureProxy(GenericDebuggerTypeProxyFixture<T> target) =>
        _value = target._rawValue;

    /// <summary>
    /// Gets the projected generic value.
    /// </summary>
    public T Value => _value;
}
