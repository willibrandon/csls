using System.Diagnostics;

namespace Csls.TestProcessHost;

/// <summary>
/// Exposes a self-referential debugger display expression for depth-bound coverage.
/// </summary>
[DebuggerDisplay("{_self}")]
internal sealed class CyclicDebuggerDisplayFixture
{
    /// <summary>
    /// Stores the cycle traversed by the debugger display expression.
    /// </summary>
    internal readonly CyclicDebuggerDisplayFixture _self;

    /// <summary>
    /// Initializes the self-referential display field.
    /// </summary>
    internal CyclicDebuggerDisplayFixture() => _self = this;
}
