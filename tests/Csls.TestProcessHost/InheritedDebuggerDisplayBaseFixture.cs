using System.Diagnostics;

namespace Csls.TestProcessHost;

/// <summary>
/// Provides an inherited debugger display over a base-class field.
/// </summary>
[DebuggerDisplay(
    "base={_baseValue}",
    Name = "inherited-{_baseValue}",
    Type = "inherited-base")]
internal class InheritedDebuggerDisplayBaseFixture
{
    /// <summary>
    /// Stores the value referenced by the inherited display template.
    /// </summary>
    internal readonly int _baseValue = 63;
}
