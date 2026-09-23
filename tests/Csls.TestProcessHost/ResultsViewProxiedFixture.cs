using System.Diagnostics;

namespace Csls.TestProcessHost;

/// <summary>
/// Associates an enumerable with a debugger proxy that suppresses Results View.
/// </summary>
[DebuggerTypeProxy(typeof(ResultsViewFixtureProxy))]
internal sealed class ResultsViewProxiedFixture : ResultsViewFixture<int>
{
    /// <summary>
    /// Creates an enumerable with data different from its debugger projection.
    /// </summary>
    internal ResultsViewProxiedFixture() : base([111])
    {
    }
}
