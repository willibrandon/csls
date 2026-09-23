using System.Diagnostics;

[assembly: DebuggerTypeProxy(
    typeof(Csls.TestProcessHost.AssemblyNamedDebuggerTypeProxyFixtureProxy),
    TargetTypeName = "Csls.TestProcessHost.AssemblyNamedDebuggerTypeProxyFixture")]

namespace Csls.TestProcessHost;

/// <summary>
/// Receives its debugger proxy declaration through an assembly target-type name.
/// </summary>
internal sealed class AssemblyNamedDebuggerTypeProxyFixture
{
    /// <summary>
    /// Stores the named assembly-targeted physical value.
    /// </summary>
    internal readonly int _rawValue = 50;
}
