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
    /// Provides a virtual dispatch slot independently of the hidden method.
    /// </summary>
    internal virtual int GetVirtualValue() => _value + 100;
}
