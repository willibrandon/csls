using Csls.Debugger.Contracts;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System.Globalization;

namespace Csls.Debugger.Evaluator.Worker;

/// <summary>
/// Lowers compiler-parsed C# expressions to the debugger expression IR.
/// </summary>
internal static class CSharpExpressionLowerer
{
    /// <summary>
    /// Parses and lowers one C# expression.
    /// </summary>
    /// <param name="expression">The C# source expression.</param>
    /// <returns>The validated language-neutral expression plan.</returns>
    internal static DebugExpressionPlan Bind(string expression)
    {
        ExpressionSyntax syntax = SyntaxFactory.ParseExpression(expression);
        Diagnostic? diagnostic = syntax.GetDiagnostics()
            .FirstOrDefault(static candidate => candidate.Severity == DiagnosticSeverity.Error);
        if (diagnostic is not null)
        {
            throw new ArgumentException(
                diagnostic.GetMessage(CultureInfo.InvariantCulture),
                nameof(expression));
        }

        return new DebugExpressionPlan(
            DebuggerEvaluatorProtocol.CurrentPlanVersion,
            DebugExpressionLanguage.CSharp,
            Lower(syntax));
    }

    private static DebugExpressionNode Lower(ExpressionSyntax syntax) => syntax switch
    {
        IdentifierNameSyntax identifier => Node(
            DebugExpressionNodeKind.Identifier,
            identifier.Identifier.ValueText),
        ThisExpressionSyntax => Node(DebugExpressionNodeKind.This),
        LiteralExpressionSyntax literal when literal.IsKind(SyntaxKind.DefaultLiteralExpression) =>
            Node(DebugExpressionNodeKind.DefaultLiteral),
        LiteralExpressionSyntax literal => ExpressionLiteral.Create(literal.Token.Value),
        ParenthesizedExpressionSyntax parenthesized => Lower(parenthesized.Expression),
        CastExpressionSyntax conversion => ConversionNode(
            conversion.Type.ToString(),
            Lower(conversion.Expression)),
        BinaryExpressionSyntax binary when binary.IsKind(SyntaxKind.IsExpression) ||
            binary.IsKind(SyntaxKind.AsExpression) => new DebugExpressionNode(
                binary.IsKind(SyntaxKind.IsExpression) ? DebugExpressionNodeKind.TypeTest : DebugExpressionNodeKind.TryCast,
                DebugExpressionOperator.None, Text: null, binary.Right.ToString(), [Lower(binary.Left)]),
        MemberAccessExpressionSyntax member
            when member.IsKind(SyntaxKind.SimpleMemberAccessExpression) => Node(
                DebugExpressionNodeKind.MemberAccess,
                member.Name.Identifier.ValueText,
                Lower(member.Expression)),
        ElementAccessExpressionSyntax element => Node(
            DebugExpressionNodeKind.ElementAccess,
            children:
            [
                Lower(element.Expression),
                .. element.ArgumentList.Arguments.Select(argument => Lower(argument.Expression))
            ]),
        ObjectCreationExpressionSyntax creation => LowerObjectCreation(creation),
        InvocationExpressionSyntax invocation => LowerInvocation(invocation),
        PrefixUnaryExpressionSyntax unary => OperatorNode(
            DebugExpressionNodeKind.Unary,
            UnaryOperator(unary.Kind()),
            Lower(unary.Operand)),
        BinaryExpressionSyntax binary => OperatorNode(
            DebugExpressionNodeKind.Binary,
            BinaryOperator(binary.Kind()),
            Lower(binary.Left),
            Lower(binary.Right)),
        ConditionalExpressionSyntax conditional => Node(
            DebugExpressionNodeKind.Conditional,
            children:
            [
                Lower(conditional.Condition),
                Lower(conditional.WhenTrue),
                Lower(conditional.WhenFalse)
            ]),
        _ => throw new NotSupportedException(
            $"C# expression kind {syntax.Kind()} is not supported by safe evaluation.")
    };

    private static DebugExpressionNode LowerObjectCreation(
        ObjectCreationExpressionSyntax creation)
    {
        if (creation.Initializer is not null)
        {
            throw new NotSupportedException(
                "C# object construction does not support object or collection initializers.");
        }

        string typeName = creation.Type.ToString();
        if (typeName.StartsWith("global::", StringComparison.Ordinal))
        {
            typeName = typeName["global::".Length..];
        }

        return Node(
            DebugExpressionNodeKind.ObjectCreation,
            typeName,
            creation.ArgumentList?.Arguments
                .Select(argument => Lower(argument.Expression))
                .ToArray() ?? []);
    }

    private static DebugExpressionNode LowerInvocation(InvocationExpressionSyntax invocation)
    {
        if (invocation.Expression is not MemberAccessExpressionSyntax member ||
            !member.IsKind(SyntaxKind.SimpleMemberAccessExpression) ||
            member.Name is not IdentifierNameSyntax method)
        {
            throw new NotSupportedException(
                "Only explicitly qualified C# instance method calls are supported.");
        }

        return Node(
            DebugExpressionNodeKind.Invocation,
            method.Identifier.ValueText,
            [
                Lower(member.Expression),
                .. invocation.ArgumentList.Arguments.Select(argument => Lower(argument.Expression))
            ]);
    }

    private static DebugExpressionOperator UnaryOperator(SyntaxKind kind) => kind switch
    {
        SyntaxKind.UnaryPlusExpression => DebugExpressionOperator.UnaryPlus,
        SyntaxKind.UnaryMinusExpression => DebugExpressionOperator.Negate,
        SyntaxKind.LogicalNotExpression => DebugExpressionOperator.LogicalNot,
        SyntaxKind.BitwiseNotExpression => DebugExpressionOperator.OnesComplement,
        _ => throw new NotSupportedException(
            $"C# unary operator {kind} is not supported by safe evaluation.")
    };

    private static DebugExpressionOperator BinaryOperator(SyntaxKind kind) => kind switch
    {
        SyntaxKind.AddExpression => DebugExpressionOperator.Add,
        SyntaxKind.SubtractExpression => DebugExpressionOperator.Subtract,
        SyntaxKind.MultiplyExpression => DebugExpressionOperator.Multiply,
        SyntaxKind.DivideExpression => DebugExpressionOperator.Divide,
        SyntaxKind.ModuloExpression => DebugExpressionOperator.Remainder,
        SyntaxKind.EqualsExpression => DebugExpressionOperator.Equal,
        SyntaxKind.NotEqualsExpression => DebugExpressionOperator.NotEqual,
        SyntaxKind.LessThanExpression => DebugExpressionOperator.LessThan,
        SyntaxKind.LessThanOrEqualExpression => DebugExpressionOperator.LessThanOrEqual,
        SyntaxKind.GreaterThanExpression => DebugExpressionOperator.GreaterThan,
        SyntaxKind.GreaterThanOrEqualExpression => DebugExpressionOperator.GreaterThanOrEqual,
        SyntaxKind.LogicalAndExpression => DebugExpressionOperator.LogicalAnd,
        SyntaxKind.LogicalOrExpression => DebugExpressionOperator.LogicalOr,
        SyntaxKind.BitwiseAndExpression => DebugExpressionOperator.BitwiseAnd,
        SyntaxKind.BitwiseOrExpression => DebugExpressionOperator.BitwiseOr,
        SyntaxKind.ExclusiveOrExpression => DebugExpressionOperator.ExclusiveOr,
        _ => throw new NotSupportedException(
            $"C# binary operator {kind} is not supported by safe evaluation.")
    };

    private static DebugExpressionNode Node(
        DebugExpressionNodeKind kind,
        string? text = null,
        params DebugExpressionNode[] children) => new(
            kind,
            DebugExpressionOperator.None,
            text,
            TypeName: null,
            children);

    private static DebugExpressionNode OperatorNode(
        DebugExpressionNodeKind kind,
        DebugExpressionOperator @operator,
        params DebugExpressionNode[] children) => new(
            kind,
            @operator,
            Text: null,
            TypeName: null,
            children);

    private static DebugExpressionNode ConversionNode(
        string typeName,
        DebugExpressionNode operand) => new(
            DebugExpressionNodeKind.Conversion,
            DebugExpressionOperator.None,
            Text: null,
            typeName,
            [operand]);
}
