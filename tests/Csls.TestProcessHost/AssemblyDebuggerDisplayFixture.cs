using System.Diagnostics;

[assembly: DebuggerDisplay(
    "assembly={_value}",
    Name = "assembly-{_value}",
    Type = "assembly-target",
    Target = typeof(Csls.TestProcessHost.AssemblyDebuggerDisplayFixture))]

namespace Csls.TestProcessHost;

/// <summary>
/// Provides a runtime type controlled by an assembly-level debugger display.
/// </summary>
internal sealed class AssemblyDebuggerDisplayFixture
{
    /// <summary>
    /// Stores the value referenced by the assembly-level display template.
    /// </summary>
    internal readonly int _value = 61;
}
