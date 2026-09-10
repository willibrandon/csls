using System.Diagnostics;

namespace Csls.TestProcessHost;

/// <summary>
/// Exposes a debugger display path that encounters a null intermediate value.
/// </summary>
[DebuggerDisplay("{_nested._value}")]
internal sealed class NullPathDebuggerDisplayFixture
{
    /// <summary>
    /// Initializes the nullable intermediate display field.
    /// </summary>
    /// <param name="nested">The nested value, or null for the fallback fixture.</param>
    internal NullPathDebuggerDisplayFixture(DebuggerDisplayNestedFixture? nested) =>
        _nested = nested;

    /// <summary>
    /// Stores the null intermediate field used by the display expression.
    /// </summary>
    internal readonly DebuggerDisplayNestedFixture? _nested;
}
