namespace Csls.TestProcessHost;

/// <summary>
/// Hides base-class field storage to distinguish runtime and explicit receiver declarations.
/// </summary>
internal sealed class ReferenceCastDerived : ReferenceCastBase
{
    /// <summary>
    /// Stores the derived declaration's independently writable value.
    /// </summary>
    internal new int _value;

    /// <summary>
    /// Hides the base method and reads only derived-declaration storage.
    /// </summary>
    internal new int GetValue() => _value;

    /// <summary>
    /// Overrides the base virtual slot using derived-declaration storage.
    /// </summary>
    internal override int GetVirtualValue() => _value + 200;

    /// <summary>
    /// Exposes a member absent from the base declaration for completion and binding tests.
    /// </summary>
    internal int GetDerivedValue() => _value;
}
