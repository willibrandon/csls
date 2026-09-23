using System.Diagnostics;

namespace Csls.TestProcessHost;

/// <summary>
/// Provides a target with an explicitly constructed debugger proxy type.
/// </summary>
[DebuggerTypeProxy(typeof(ClosedGenericDebuggerTypeProxyFixtureProxy<int[]>))]
internal sealed class ClosedGenericDebuggerTypeProxyFixture
{
    /// <summary>
    /// Stores the physical array projected through the closed generic proxy.
    /// </summary>
    internal readonly int[] _rawValue = [52];
}
