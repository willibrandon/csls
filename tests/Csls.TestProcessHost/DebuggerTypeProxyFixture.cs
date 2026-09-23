using System.Diagnostics;

namespace Csls.TestProcessHost;

/// <summary>
/// Provides physical fields hidden behind a debugger-owned type proxy.
/// </summary>
[DebuggerTypeProxy(typeof(DebuggerTypeProxyFixtureProxy))]
internal sealed class DebuggerTypeProxyFixture
{
    /// <summary>
    /// Stores the physical value exposed only through Raw View.
    /// </summary>
    internal readonly int _rawValue = 41;
}
