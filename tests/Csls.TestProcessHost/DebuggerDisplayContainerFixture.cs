namespace Csls.TestProcessHost;

/// <summary>
/// Groups debugger-display fixtures for child-row presentation coverage.
/// </summary>
internal sealed class DebuggerDisplayContainerFixture
{
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
}
