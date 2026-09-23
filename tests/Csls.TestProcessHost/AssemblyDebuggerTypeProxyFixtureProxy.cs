namespace Csls.TestProcessHost;

/// <summary>
/// Projects a target selected by an assembly-level debugger proxy declaration.
/// </summary>
internal sealed class AssemblyDebuggerTypeProxyFixtureProxy
{
    /// <summary>
    /// Creates the assembly-targeted debugger projection.
    /// </summary>
    /// <param name="target">The original assembly-targeted object.</param>
    internal AssemblyDebuggerTypeProxyFixtureProxy(AssemblyDebuggerTypeProxyFixture target) =>
        Value = target._rawValue;

    /// <summary>
    /// Gets the projected assembly-targeted value.
    /// </summary>
    public readonly int Value;
}
