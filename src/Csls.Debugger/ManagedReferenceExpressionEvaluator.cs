using Csls.Debugger.Contracts;

namespace Csls.Debugger;

/// <summary>
/// Applies non-executing reference type operations while preserving declared types and existing runtime values.
/// </summary>
internal sealed class ManagedReferenceExpressionEvaluator
{
    private readonly ManagedReferenceConversion _conversions;

    /// <summary>
    /// Creates an evaluator over the session's exact loaded-type conversion policy.
    /// </summary>
    internal ManagedReferenceExpressionEvaluator(ManagedReferenceConversion conversions)
    {
        ArgumentNullException.ThrowIfNull(conversions);
        _conversions = conversions;
    }

    /// <summary>
    /// Evaluates a cast or type test without changing the referenced object or invoking target code.
    /// </summary>
    internal ManagedExpressionValue Evaluate(
        ManagedExpressionValue operand,
        ManagedBoundType? declaredSource,
        ManagedBoundType? actualSource,
        ManagedBoundType target,
        string targetDisplayName,
        DebugExpressionNodeKind kind,
        nint thread)
    {
        bool isNull = operand is { HasScalar: true, Scalar: null };
        if (kind == DebugExpressionNodeKind.TypeTest)
        {
            return ManagedExpressionValueFactory.FromScalar(
                !isNull && actualSource is not null && _conversions.IsRuntimeAssignable(actualSource, target, thread), "bool");
        }

        if (!target.IsReference)
        {
            if (kind != DebugExpressionNodeKind.TryCast && !isNull && operand.HasScalar &&
                actualSource is not null && actualSource.IsSameType(target))
            {
                return operand with { DeclaredType = target, ExplicitReceiverType = target };
            }

            throw new InvalidOperationException(
                $"The type operation cannot convert this value to '{targetDisplayName}' without supported value materialization.");
        }

        if (declaredSource is not null && !(kind == DebugExpressionNodeKind.ReferenceUpcast
            ? _conversions.IsImplicit(declaredSource, target, thread)
            : _conversions.IsExplicit(declaredSource, target, thread)))
        {
            throw new InvalidOperationException(
                $"No built-in reference conversion exists from '{declaredSource.DisplayName}' to '{target.DisplayName}'.");
        }

        bool matches = !isNull && actualSource is not null &&
            _conversions.IsRuntimeAssignable(actualSource, target, thread);
        if (isNull || !matches && kind == DebugExpressionNodeKind.TryCast)
        {
            return ManagedExpressionValueFactory.FromScalar(value: null, targetDisplayName) with
            {
                DeclaredType = target,
                ExplicitReceiverType = target
            };
        }

        if (!matches)
        {
            throw new InvalidOperationException(
                $"The runtime value of type '{actualSource?.DisplayName ?? operand.Type}' cannot be cast to '{target.DisplayName}'.");
        }

        return operand with { DeclaredType = target, ExplicitReceiverType = target };
    }
}
