namespace Csls.Debugger;

/// <summary>
/// Identifies a source parameter stored in a compiler-generated state-machine field.
/// </summary>
/// <param name="Name">The source parameter or receiver name.</param>
/// <param name="FieldName">The runtime field carrying the active parameter value.</param>
/// <param name="MethodToken">The original source method's metadata token.</param>
/// <param name="ParameterIndex">The zero-based source parameter index, or null for the receiver.</param>
/// <param name="TupleCustomTypeInfo">The tuple names declared on the source parameter.</param>
internal sealed record ManagedCapturedArgument(
    string Name,
    string FieldName,
    uint MethodToken,
    int? ParameterIndex,
    ManagedTupleCustomTypeInfo? TupleCustomTypeInfo);
