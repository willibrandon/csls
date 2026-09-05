using System.Diagnostics;

namespace Csls.TestProcessHost;

/// <summary>
/// Declares an inherited debugger proxy for derived runtime objects.
/// </summary>
[DebuggerTypeProxy(typeof(InheritedDebuggerTypeProxyFixtureProxy))]
internal class InheritedDebuggerTypeProxyBaseFixture
{
    /// <summary>
    /// Stores the physical base value.
    /// </summary>
    internal readonly int _baseValue = 45;
}
