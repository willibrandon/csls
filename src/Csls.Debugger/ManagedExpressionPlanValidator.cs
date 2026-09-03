using Csls.Debugger.Contracts;

namespace Csls.Debugger;

/// <summary>
/// Validates expression plans crossing the managed evaluator process boundary.
/// </summary>
internal static class ManagedExpressionPlanValidator
{
    private const int MaximumDepth = 64;
    private const int MaximumNodes = 1024;
    private const int MaximumTextLength = 1024 * 1024;

    /// <summary>
    /// Validates plan version, language identity, shape, and complexity limits.
    /// </summary>
    /// <param name="plan">The deserialized expression plan.</param>
    /// <param name="expectedLanguage">The source language recorded in the selected frame.</param>
    internal static void Validate(
        DebugExpressionPlan plan,
        DebugExpressionLanguage expectedLanguage)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (plan.Version != DebuggerEvaluatorProtocol.CurrentPlanVersion)
        {
            throw new InvalidDataException(
                $"Expression plan version {plan.Version} is incompatible with " +
                $"{DebuggerEvaluatorProtocol.CurrentPlanVersion}.");
        }

        if (plan.Language != expectedLanguage)
        {
            throw new InvalidDataException(
                $"Expression plan language {plan.Language} does not match frame language " +
                $"{expectedLanguage}.");
        }

        int nodeCount = 0;
        ValidateNode(plan.Root, depth: 0, ref nodeCount);
    }

    private static void ValidateNode(DebugExpressionNode node, int depth, ref int nodeCount)
    {
        ArgumentNullException.ThrowIfNull(node);
        if (depth > MaximumDepth || ++nodeCount > MaximumNodes)
        {
            throw new InvalidDataException(
                "The expression plan exceeds the safe evaluator complexity limit.");
        }

        IReadOnlyList<DebugExpressionNode> children = node.Children ??
            throw new InvalidDataException("An expression node has no child collection.");
        if (node.Text is { Length: > MaximumTextLength })
        {
            throw new InvalidDataException(
                "The expression plan contains text beyond the safe evaluator limit.");
        }

        int expectedChildren = node.Kind switch
        {
            DebugExpressionNodeKind.Identifier or
            DebugExpressionNodeKind.This or
            DebugExpressionNodeKind.Literal => 0,
            DebugExpressionNodeKind.MemberAccess or
            DebugExpressionNodeKind.Unary => 1,
            DebugExpressionNodeKind.Binary => 2,
            DebugExpressionNodeKind.Conditional => 3,
            DebugExpressionNodeKind.Invocation when children.Count >= 1 => children.Count,
            DebugExpressionNodeKind.Invocation => throw new InvalidDataException(
                "An invocation requires an instance receiver."),
            DebugExpressionNodeKind.ElementAccess when children.Count >= 2 => children.Count,
            DebugExpressionNodeKind.ElementAccess => throw new InvalidDataException(
                "An element access requires a receiver and at least one index."),
            _ => throw new InvalidDataException(
                $"Expression node kind {node.Kind} is not supported.")
        };
        if (children.Count != expectedChildren)
        {
            throw new InvalidDataException(
                $"Expression node {node.Kind} has {children.Count} children; " +
                $"expected {expectedChildren}.");
        }

        if ((node.Kind is DebugExpressionNodeKind.Identifier or
            DebugExpressionNodeKind.MemberAccess or
            DebugExpressionNodeKind.Invocation) && string.IsNullOrWhiteSpace(node.Text))
        {
            throw new InvalidDataException(
                $"Expression node {node.Kind} requires a source name.");
        }

        ValidateOperator(node);
        foreach (DebugExpressionNode child in children)
        {
            ValidateNode(child, depth + 1, ref nodeCount);
        }
    }

    private static void ValidateOperator(DebugExpressionNode node)
    {
        bool valid = node.Kind switch
        {
            DebugExpressionNodeKind.Unary => node.Operator is
                DebugExpressionOperator.UnaryPlus or
                DebugExpressionOperator.Negate or
                DebugExpressionOperator.LogicalNot or
                DebugExpressionOperator.OnesComplement,
            DebugExpressionNodeKind.Binary => node.Operator is
                DebugExpressionOperator.Add or
                DebugExpressionOperator.Subtract or
                DebugExpressionOperator.Multiply or
                DebugExpressionOperator.Divide or
                DebugExpressionOperator.Remainder or
                DebugExpressionOperator.Equal or
                DebugExpressionOperator.NotEqual or
                DebugExpressionOperator.LessThan or
                DebugExpressionOperator.LessThanOrEqual or
                DebugExpressionOperator.GreaterThan or
                DebugExpressionOperator.GreaterThanOrEqual or
                DebugExpressionOperator.LogicalAnd or
                DebugExpressionOperator.LogicalOr or
                DebugExpressionOperator.BitwiseAnd or
                DebugExpressionOperator.BitwiseOr or
                DebugExpressionOperator.ExclusiveOr,
            _ => node.Operator == DebugExpressionOperator.None
        };
        if (!valid)
        {
            throw new InvalidDataException(
                $"Operator {node.Operator} is invalid for expression node {node.Kind}.");
        }
    }
}
