using System.Diagnostics;

namespace Csls.TestProcessHost;

/// <summary>
/// Overrides inherited debugger display metadata at the most-derived type.
/// </summary>
[DebuggerDisplay(
    "derived={_derivedValue}",
    Name = "derived-{_derivedValue}",
    Type = "derived-display")]
internal sealed class OverriddenDebuggerDisplayFixture : InheritedDebuggerDisplayBaseFixture
{
    /// <summary>
    /// Stores the value referenced by the most-derived display template.
    /// </summary>
    internal readonly int _derivedValue = 65;
}
