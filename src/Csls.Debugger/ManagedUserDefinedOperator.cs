namespace Csls.Debugger;

/// <summary>
/// Describes one loaded user-defined operator selected for target execution.
/// </summary>
/// <param name="DeclaringType">The closed type that declares the operator.</param>
/// <param name="MethodToken">The aggregate metadata token for the operator.</param>
/// <param name="ParameterTypes">The exact loaded operator parameter types.</param>
/// <param name="ResultType">The exact loaded operator result type.</param>
internal sealed record ManagedUserDefinedOperator(
    ManagedBoundType DeclaringType,
    uint MethodToken,
    IReadOnlyList<ManagedBoundType> ParameterTypes,
    ManagedBoundType ResultType);
