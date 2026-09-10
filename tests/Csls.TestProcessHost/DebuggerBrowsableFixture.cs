using System.Diagnostics;

namespace Csls.TestProcessHost;

/// <summary>
/// Provides stable debugger-presentation metadata over ordinary runtime fields.
/// </summary>
internal sealed class DebuggerBrowsableFixture
{
    /// <summary>
    /// Initializes the fixture and its self-referential recursion boundary.
    /// </summary>
    internal DebuggerBrowsableFixture() => _self = this;

    /// <summary>
    /// Stores an ordinary field shown in the default and raw debugger views.
    /// </summary>
    internal readonly int _visible = 46;

    /// <summary>
    /// Stores a field hidden from the default debugger view.
    /// </summary>
    [DebuggerBrowsable(DebuggerBrowsableState.Never)]
    internal readonly int _hidden = 47;

    /// <summary>
    /// Stores an explicitly collapsed field shown as an ordinary member.
    /// </summary>
    [DebuggerBrowsable(DebuggerBrowsableState.Collapsed)]
    internal readonly int _collapsed = 48;

    /// <summary>
    /// Stores children flattened into the default debugger view.
    /// </summary>
    [DebuggerBrowsable(DebuggerBrowsableState.RootHidden)]
    internal readonly int[] _rootItems = [49, 50];

    /// <summary>
    /// Stores an object whose fields are flattened into the default view.
    /// </summary>
    [DebuggerBrowsable(DebuggerBrowsableState.RootHidden)]
    internal readonly DebuggerBrowsableNestedFixture _rootObject = new();

    /// <summary>
    /// Stores a non-expandable root-hidden value that remains directly inspectable.
    /// </summary>
    [DebuggerBrowsable(DebuggerBrowsableState.RootHidden)]
    internal readonly int _scalarRoot = 52;

    /// <summary>
    /// Stores a root-hidden cycle that remains bounded and directly inspectable.
    /// </summary>
    [DebuggerBrowsable(DebuggerBrowsableState.RootHidden)]
    internal readonly DebuggerBrowsableFixture _self;

    /// <summary>
    /// Stores a null root-hidden value that remains directly inspectable.
    /// </summary>
    [DebuggerBrowsable(DebuggerBrowsableState.RootHidden)]
    internal readonly DebuggerBrowsableNestedFixture? _missing = null;
}
