namespace Csls.TestProcessHost;

/// <summary>
/// Provides base-class field storage for explicit receiver-type debugger expressions.
/// </summary>
internal class ReferenceCastBase
{
    /// <summary>
    /// Stores the base declaration's independently writable value.
    /// </summary>
    internal int _value;

    /// <summary>
    /// Reads only base-declaration storage through a nonvirtual method.
    /// </summary>
    internal int GetValue() => _value;

    /// <summary>
    /// Evaluates the base declaration's nonvirtual property through its method body.
    /// </summary>
    internal int ValueProperty => GetValue();

    /// <summary>
    /// Provides a property slot for runtime dispatch through an explicit base cast.
    /// </summary>
    internal virtual int VirtualValueProperty => _value + 100;

    /// <summary>
    /// Provides a virtual dispatch slot independently of the hidden method.
    /// </summary>
    internal virtual int GetVirtualValue() => _value + 100;
}
