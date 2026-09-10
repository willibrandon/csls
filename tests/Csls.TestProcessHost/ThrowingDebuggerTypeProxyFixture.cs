using System.Diagnostics;

namespace Csls.TestProcessHost;

/// <summary>
/// Provides an original value recovered after debugger proxy construction throws.
/// </summary>
[DebuggerTypeProxy(typeof(ThrowingDebuggerTypeProxyFixtureProxy))]
internal sealed class ThrowingDebuggerTypeProxyFixture
{
    /// <summary>
    /// Stores the physical value used after proxy construction fails.
    /// </summary>
    internal readonly int _rawValue = 48;
}
