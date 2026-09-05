using System.Diagnostics;

namespace Csls.TestProcessHost;

/// <summary>
/// Remains enumerable when its declared debugger proxy cannot be constructed.
/// </summary>
[DebuggerTypeProxy(typeof(ResultsViewFailedProxyFixtureProxy))]
internal sealed class ResultsViewFailedProxyFixture : ResultsViewFixture<int>
{
    /// <summary>
    /// Creates values available independently of the failing debugger proxy.
    /// </summary>
    internal ResultsViewFailedProxyFixture() : base([181, 182])
    {
    }
}
