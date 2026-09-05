using System.Diagnostics;

[assembly: DebuggerTypeProxy(
    typeof(Csls.TestProcessHost.AssemblyDebuggerTypeProxyFixtureProxy),
    Target = typeof(Csls.TestProcessHost.AssemblyDebuggerTypeProxyFixture))]

namespace Csls.TestProcessHost;

/// <summary>
/// Receives its debugger proxy declaration from assembly metadata.
/// </summary>
internal sealed class AssemblyDebuggerTypeProxyFixture
{
    /// <summary>
    /// Stores the assembly-targeted physical value.
    /// </summary>
    internal readonly int _rawValue = 47;
}
