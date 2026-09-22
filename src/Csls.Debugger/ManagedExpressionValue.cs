using Csls.Debugger.Contracts;

namespace Csls.Debugger;

/// <summary>
/// Carries one runtime-backed or computed value through safe expression binding.
/// </summary>
/// <param name="Display">The debugger-facing value description.</param>
/// <param name="Scalar">The decoded primitive value when one is available.</param>
/// <param name="HasScalar">Whether <paramref name="Scalar"/> is authoritative.</param>
/// <param name="Type">The semantic runtime or computed type before presentation transforms.</param>
/// <param name="RuntimeValueReference">The internal retained runtime identity, or zero for a computed value.</param>
/// <param name="IsContextualDefault">Whether the value still requires a type from its expression context.</param>
/// <param name="DeclaredType">The exact declared source type, including typed null values.</param>
/// <param name="ExplicitReceiverType">The receiver declaration selected by an explicit cast for member binding.</param>
/// <param name="IsZeroValueTypeDefault">Whether the value is an exact zero-initialized struct argument.</param>
/// <param name="IsTypedDefault">Whether the expression explicitly names the type of a default value.</param>
/// <param name="IsNullableValue">Whether the value carries a non-executing nullable projection.</param>
/// <param name="RequiresNullableMaterialization">Whether a later call must build exact nullable argument storage.</param>
/// <param name="RequiresBoxing">Whether a later call must allocate an exact boxed argument copy.</param>
/// <param name="BoxingType">The exact value type allocated for a boxing conversion.</param>
/// <param name="BoxesNullableAsNull">Whether nullable boxing produces a null reference.</param>
internal sealed record ManagedExpressionValue(
    DebugVariableInfo Display,
    object? Scalar,
    bool HasScalar,
    string Type,
    int RuntimeValueReference = 0,
    bool IsContextualDefault = false,
    ManagedBoundType? DeclaredType = null,
    ManagedBoundType? ExplicitReceiverType = null,
    bool IsZeroValueTypeDefault = false,
    bool IsTypedDefault = false,
    bool IsNullableValue = false,
    bool RequiresNullableMaterialization = false,
    bool RequiresBoxing = false,
    ManagedBoundType? BoxingType = null,
    bool BoxesNullableAsNull = false)
{
    /// <summary>
    /// Converts the internal value to the protocol-neutral evaluation result.
    /// </summary>
    /// <returns>The debugger-facing evaluation result.</returns>
    internal DebugEvaluateResult ToResult() => IsContextualDefault
        ? throw new InvalidOperationException("A default literal requires a destination type.")
        : new(
            Display.Value,
            Display.Type,
            Display.VariablesReference,
            Display.MemoryReference);
}
