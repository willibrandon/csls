namespace Csls.Debugger;

/// <summary>
/// Identifies an exact property accessor and its proven field-backed read when available.
/// </summary>
/// <param name="MethodToken">The accessor's exact method definition token.</param>
/// <param name="MethodName">The metadata name of the getter.</param>
/// <param name="FieldToken">The existing field returned by a proven pure getter.</param>
/// <param name="TupleCustomTypeInfo">The tuple names declared by the property.</param>
internal sealed record ManagedPropertyGetter(
    uint MethodToken,
    string MethodName,
    uint? FieldToken,
    ManagedTupleCustomTypeInfo? TupleCustomTypeInfo);
