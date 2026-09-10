namespace Csls.TestProcessHost;

/// <summary>
/// Projects a target selected by an assembly-level target-type name.
/// </summary>
internal sealed class AssemblyNamedDebuggerTypeProxyFixtureProxy
{
    /// <summary>
    /// Creates the named assembly-targeted debugger projection.
    /// </summary>
    /// <param name="target">The original assembly-targeted object.</param>
    internal AssemblyNamedDebuggerTypeProxyFixtureProxy(
        AssemblyNamedDebuggerTypeProxyFixture target) => Value = target._rawValue;

    /// <summary>
    /// Gets the named assembly-targeted projected value.
    /// </summary>
    public readonly int Value;
}
