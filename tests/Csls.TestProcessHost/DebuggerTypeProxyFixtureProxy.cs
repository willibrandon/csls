using System.Diagnostics;

namespace Csls.TestProcessHost;

/// <summary>
/// Projects public debugger fields for <see cref="DebuggerTypeProxyFixture"/>.
/// </summary>
internal sealed class DebuggerTypeProxyFixtureProxy
{
    /// <summary>
    /// Creates the debugger projection through a non-public constructor.
    /// </summary>
    /// <param name="target">The original runtime object.</param>
    private DebuggerTypeProxyFixtureProxy(DebuggerTypeProxyFixture target)
    {
        Value = target._rawValue + 1;
        Items = [target._rawValue + 2, target._rawValue + 3];
        _privateValue = target._rawValue + 4;
    }

    /// <summary>
    /// Gets the primary projected value.
    /// </summary>
    public readonly int Value;

    /// <summary>
    /// Gets projected items flattened into the proxy root.
    /// </summary>
    [DebuggerBrowsable(DebuggerBrowsableState.RootHidden)]
    public readonly int[] Items;

    private readonly int _privateValue;
}
