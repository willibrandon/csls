using Csls.Debugger.Contracts;

namespace Csls.Debugger;

/// <summary>
/// Describes one exact loaded implicit conversion operator selected before target execution.
/// </summary>
/// <param name="DeclaringType">The closed type that declares the conversion operator.</param>
/// <param name="MethodToken">The aggregate metadata token for the conversion operator.</param>
/// <param name="ParameterType">The exact loaded operator parameter type.</param>
/// <param name="ResultType">The exact loaded operator result type.</param>
/// <param name="TargetType">The exact loaded call parameter receiving the converted result.</param>
/// <param name="Language">The source language controlling standard numeric conversions.</param>
/// <param name="IsLifted">Whether nullable source semantics wrap the operator execution.</param>
internal sealed record ManagedUserDefinedConversion(
    ManagedBoundType DeclaringType,
    uint MethodToken,
    ManagedBoundType ParameterType,
    ManagedBoundType ResultType,
    ManagedBoundType TargetType,
    DebugExpressionLanguage Language,
    bool IsLifted);
