using System.Diagnostics;

namespace Csls.TestProcessHost;

/// <summary>
/// Groups debugger-display fixtures for child-row presentation coverage.
/// </summary>
internal sealed class DebuggerDisplayContainerFixture
{
    /// <summary>
    /// Stores a value whose member display takes precedence over its type display.
    /// </summary>
    [DebuggerDisplay(
        "member={_memberPrimitive}",
        Name = "member-{_memberPrimitive}",
        Type = "member-display")]
    internal readonly DebuggerDisplayFixture _member = new();

    /// <summary>
    /// Stores a primitive whose member display replaces its ordinary presentation.
    /// </summary>
    [DebuggerDisplay(
        "member-int={_direct._id}",
        Name = "member-number",
        Type = "member-int-type")]
    internal readonly int _memberPrimitive = 73;

    /// <summary>
    /// Stores a member whose display would require target-code execution.
    /// </summary>
    [DebuggerDisplay("{Computed}", Name = "{Computed}", Type = "{Computed}")]
    internal readonly int _unsafeMember = 74;

    /// <summary>
    /// Counts attempts to execute the unsafe member-display property.
    /// </summary>
    internal int _memberDisplayAccessCount;

    private int Computed
    {
        get
        {
            _memberDisplayAccessCount++;
            return 75;
        }
    }

    /// <summary>
    /// Stores a directly attributed display value.
    /// </summary>
    internal readonly DebuggerDisplayFixture _direct = new();

    /// <summary>
    /// Stores an assembly-targeted display value.
    /// </summary>
    internal readonly AssemblyDebuggerDisplayFixture _assembly = new();

    /// <summary>
    /// Stores a display selected by an assembly target type name.
    /// </summary>
    internal readonly AssemblyNamedDebuggerDisplayFixture _assemblyNamed = new();

    /// <summary>
    /// Stores a value with inherited display metadata.
    /// </summary>
    internal readonly InheritedDebuggerDisplayFixture _inherited = new();

    /// <summary>
    /// Stores a value whose derived display overrides inherited metadata.
    /// </summary>
    internal readonly OverriddenDebuggerDisplayFixture _overridden = new();

    /// <summary>
    /// Stores a value whose display would require target-code execution.
    /// </summary>
    internal readonly UnsafeDebuggerDisplayFixture _unsafe = new();

    /// <summary>
    /// Stores a value with safe required and unsafe optional display templates.
    /// </summary>
    internal readonly PartiallyUnsafeDebuggerDisplayFixture _partiallyUnsafe = new();

    /// <summary>
    /// Stores a value with malformed display metadata.
    /// </summary>
    internal readonly MalformedDebuggerDisplayFixture _malformed = new();

    /// <summary>
    /// Stores a value whose display references an unknown field.
    /// </summary>
    internal readonly MissingDebuggerDisplayFixture _missing = new();

    /// <summary>
    /// Stores a value whose display path encounters null.
    /// </summary>
    internal readonly NullPathDebuggerDisplayFixture _nullPath = new(nested: null);

    /// <summary>
    /// Stores a value with a cyclic display expression.
    /// </summary>
    internal readonly CyclicDebuggerDisplayFixture _cyclic = new();

    /// <summary>
    /// Stores a value whose display components are explicitly empty.
    /// </summary>
    internal readonly EmptyDebuggerDisplayFixture _empty = new();

    /// <summary>
    /// Stores a value whose display components are serialized null values.
    /// </summary>
    internal readonly NullDebuggerDisplayFixture _null = new();

    /// <summary>
    /// Stores a value with multiple attributes whose first display must win.
    /// </summary>
    internal readonly MultipleDebuggerDisplayFixture _multiple = new();
}
