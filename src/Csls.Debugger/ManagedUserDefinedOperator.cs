using Csls.Debugger.Contracts;

namespace Csls.Debugger;

/// <summary>
/// Describes one loaded user-defined operator selected for target execution.
/// </summary>
/// <param name="DeclaringType">The closed type that declares the operator.</param>
/// <param name="MethodToken">The aggregate metadata token for the operator.</param>
/// <param name="ParameterTypes">The exact loaded operator parameter types.</param>
/// <param name="ResultType">The exact loaded operator result type.</param>
/// <param name="OperandTypes">The effective source-language operand types.</param>
/// <param name="ExpressionResultType">The effective source-language result type.</param>
/// <param name="IsLifted">Whether nullable operands wrap the underlying operator execution.</param>
/// <param name="Language">The source language controlling standard operand conversions.</param>
internal sealed record ManagedUserDefinedOperator(
    ManagedBoundType DeclaringType,
    uint MethodToken,
    IReadOnlyList<ManagedBoundType> ParameterTypes,
    ManagedBoundType ResultType,
    IReadOnlyList<ManagedBoundType> OperandTypes,
    ManagedBoundType ExpressionResultType,
    bool IsLifted,
    DebugExpressionLanguage Language);
