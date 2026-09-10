using System.Diagnostics;

[assembly: DebuggerDisplay(
    "named-target={_value}",
    Name = "named-target-{_value}",
    Type = "assembly-named-target",
    TargetTypeName = "Csls.TestProcessHost.AssemblyNamedDebuggerDisplayFixture")]

namespace Csls.TestProcessHost;

/// <summary>
/// Provides a runtime type selected by an assembly display target name.
/// </summary>
internal sealed class AssemblyNamedDebuggerDisplayFixture
{
    /// <summary>
    /// Stores the value referenced by the named assembly display template.
    /// </summary>
    internal readonly int _value = 62;
}
