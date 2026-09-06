namespace Csls.Debugger;

/// <summary>
/// Identifies a source variable stored in a compiler-generated state-machine field.
/// </summary>
/// <param name="Name">The source local, parameter, or receiver name.</param>
/// <param name="FieldName">The runtime field carrying the active variable value.</param>
/// <param name="MethodToken">The original source method's metadata token.</param>
/// <param name="ParameterIndex">The source parameter index, or null for a local or receiver.</param>
/// <param name="TupleCustomTypeInfo">The tuple names declared on the source variable.</param>
internal sealed record ManagedStateMachineVariable(
    string Name,
    string FieldName,
    uint MethodToken,
    int? ParameterIndex,
    ManagedTupleCustomTypeInfo? TupleCustomTypeInfo);
