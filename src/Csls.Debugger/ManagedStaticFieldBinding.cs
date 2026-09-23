using System.Reflection;

namespace Csls.Debugger;

/// <summary>
/// Identifies a static declaration and its closed storage type independently of native pointer lifetime.
/// </summary>
/// <param name="DeclaringType">The exact loaded type that declares the field.</param>
/// <param name="FieldToken">The field-definition metadata token.</param>
/// <param name="FieldType">The substituted field type.</param>
/// <param name="Attributes">The declaration's storage and mutability flags.</param>
/// <param name="TupleCustomTypeInfo">The field's authored tuple element names.</param>
/// <param name="Constant">The decoded metadata literal when the field is constant.</param>
internal sealed record ManagedStaticFieldBinding(
    ManagedBoundType DeclaringType,
    uint FieldToken,
    ManagedBoundType FieldType,
    FieldAttributes Attributes,
    ManagedTupleCustomTypeInfo? TupleCustomTypeInfo,
    object? Constant)
{
    /// <summary>
    /// Gets whether this declaration carries a metadata constant instead of runtime storage.
    /// </summary>
    internal bool IsLiteral => (Attributes & FieldAttributes.Literal) != 0;

    /// <summary>
    /// Gets whether ordinary source assignment may replace this field's storage.
    /// </summary>
    internal bool IsWritable => (Attributes & (FieldAttributes.Literal | FieldAttributes.InitOnly)) == 0;
}
