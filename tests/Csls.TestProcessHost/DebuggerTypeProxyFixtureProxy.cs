using System.Diagnostics;

namespace Csls.TestProcessHost;

/// <summary>
/// Projects public debugger fields for <see cref="DebuggerTypeProxyFixture"/>.
/// </summary>
internal class DebuggerTypeProxyFixtureProxy
{
    /// <summary>
    /// Creates the debugger projection through a non-public constructor.
    /// </summary>
    /// <param name="target">The original runtime object.</param>
    protected DebuggerTypeProxyFixtureProxy(DebuggerTypeProxyFixture target)
    {
        Value = target._rawValue + 1;
        Items = [target._rawValue + 2, target._rawValue + 3];
        ProtectedValue = target._rawValue + 4;
        _attributedValue = target._rawValue + 5;
        HiddenValue = target._rawValue + 6;
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

    /// <summary>
    /// Gets the protected value exposed by standard debugger proxy semantics.
    /// </summary>
    protected readonly int ProtectedValue;

    /// <summary>
    /// Gets the attributed private value intentionally exposed by the proxy.
    /// </summary>
    [DebuggerBrowsable(DebuggerBrowsableState.Collapsed)]
    private readonly int _attributedValue;

    /// <summary>
    /// Gets the public value intentionally hidden by the proxy.
    /// </summary>
    [DebuggerBrowsable(DebuggerBrowsableState.Never)]
    public readonly int HiddenValue;

    /// <summary>
    /// Gets a computed value through target-code property evaluation.
    /// </summary>
    public int ComputedValue => Value + 10;

    /// <summary>
    /// Gets an expandable array retained across subsequent property evaluations.
    /// </summary>
    public int[] ArrayValue => Items;

    /// <summary>
    /// Gets a protected boxed value through target-code property evaluation.
    /// </summary>
    protected object BoxedValue => ProtectedValue + 10;

    /// <summary>
    /// Gets an attributed private property intentionally exposed by the proxy.
    /// </summary>
    [DebuggerBrowsable(DebuggerBrowsableState.Collapsed)]
    private int _attributedProperty => Value + 5;

    /// <summary>
    /// Gets a property whose target exception remains isolated to its debugger row.
    /// </summary>
    public int ThrowingValue => throw new InvalidOperationException(
        $"Expected debugger proxy getter failure for value {Value}.");

    /// <summary>
    /// Gets a hidden property that must never execute during proxy presentation.
    /// </summary>
    [DebuggerBrowsable(DebuggerBrowsableState.Never)]
    public int HiddenProperty => throw new InvalidOperationException(
        $"A hidden debugger proxy property must not execute for value {Value}.");

    /// <summary>
    /// Gets an indexed value that must not appear in proxy presentation.
    /// </summary>
    /// <param name="index">The requested index.</param>
    /// <returns>The requested projected value.</returns>
    public int this[int index] => Value + index;

    private readonly int _privateValue;
}
