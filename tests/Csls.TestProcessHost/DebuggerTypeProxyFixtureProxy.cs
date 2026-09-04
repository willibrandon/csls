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
        _rootHiddenScalar = target._rawValue + 9;
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
    /// Gets projected children that appear directly in the proxy expansion.
    /// </summary>
    [DebuggerBrowsable(DebuggerBrowsableState.RootHidden)]
    private int[] RootHiddenValues => [Value + 6, Value + 7];

    /// <summary>
    /// Gets a scalar root-hidden property that contributes no debugger rows.
    /// </summary>
    [DebuggerBrowsable(DebuggerBrowsableState.RootHidden)]
    private int RootHiddenScalar => Value + 8;

    /// <summary>
    /// Stores a scalar root-hidden field that contributes no debugger rows.
    /// </summary>
    [DebuggerBrowsable(DebuggerBrowsableState.RootHidden)]
    private readonly int _rootHiddenScalar;

    /// <summary>
    /// Gets a static proxy field shown through the synthetic static container.
    /// </summary>
    public static readonly int s_staticField = CreateStaticValue(60);

    /// <summary>
    /// Gets static field children flattened inside the synthetic static container.
    /// </summary>
    [DebuggerBrowsable(DebuggerBrowsableState.RootHidden)]
    private static readonly int[] s_staticFieldItems = [61, 62];

    /// <summary>
    /// Gets a static proxy property evaluated under the guarded target-code policy.
    /// </summary>
    public static int StaticProperty =>
        s_staticField + s_staticFieldItems.Length + s_attributedStatic - 65;

    /// <summary>
    /// Gets static property children flattened inside the synthetic static container.
    /// </summary>
    [DebuggerBrowsable(DebuggerBrowsableState.RootHidden)]
    private static int[] StaticPropertyItems => [64, 65];

    /// <summary>
    /// Gets a static property whose target exception remains isolated to its debugger row.
    /// </summary>
    public static int ThrowingStatic => throw new InvalidOperationException(
        "Expected static debugger proxy getter failure.");

    /// <summary>
    /// Gets an attributed private static field shown in the static container.
    /// </summary>
    [DebuggerBrowsable(DebuggerBrowsableState.Collapsed)]
    private static readonly int s_attributedStatic = CreateStaticValue(66);

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

    private static int CreateStaticValue(int value) => value;
}
