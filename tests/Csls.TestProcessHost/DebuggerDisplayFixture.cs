using System.Diagnostics;

namespace Csls.TestProcessHost;

/// <summary>
/// Provides debugger display templates over direct and nested runtime fields.
/// </summary>
[DebuggerDisplay(
    "{{id}}={this._id}; label={_label,nq}; nested={_nested._value}",
    Name = "child-{_id}",
    Type = "display-{_kind}")]
internal sealed class DebuggerDisplayFixture
{
    /// <summary>
    /// Stores the identity rendered into the value and child name.
    /// </summary>
    internal readonly int _id = 54;

    /// <summary>
    /// Stores the discriminator rendered into the type column.
    /// </summary>
    internal readonly int _kind = 7;

    /// <summary>
    /// Stores escaped text rendered without debugger quotes.
    /// </summary>
    internal readonly string _label = "alpha\nbeta";

    /// <summary>
    /// Stores the nested object traversed by the display expression.
    /// </summary>
    internal readonly DebuggerDisplayNestedFixture _nested = new();
}
