using Csls.Debugger.Contracts;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.VisualBasic;
using Microsoft.CodeAnalysis.VisualBasic.Syntax;
using System.Globalization;

namespace Csls.Debugger.Evaluator.Worker;

/// <summary>
/// Lowers compiler-parsed Visual Basic expressions to the debugger expression IR.
/// </summary>
internal static class VisualBasicExpressionLowerer
{
    /// <summary>
    /// Parses and lowers one Visual Basic expression.
    /// </summary>
    /// <param name="expression">The Visual Basic source expression.</param>
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
            DebugExpressionLanguage.VisualBasic,
            Lower(syntax));
    }

    private static DebugExpressionNode Lower(ExpressionSyntax syntax) => syntax switch
    {
        IdentifierNameSyntax identifier => Node(
            DebugExpressionNodeKind.Identifier,
            identifier.Identifier.ValueText),
        MeExpressionSyntax => Node(DebugExpressionNodeKind.This),
        LiteralExpressionSyntax literal => ExpressionLiteral.Create(literal.Token.Value),
        ParenthesizedExpressionSyntax parenthesized => Lower(parenthesized.Expression),
        MemberAccessExpressionSyntax member => Node(
            DebugExpressionNodeKind.MemberAccess,
            member.Name.Identifier.ValueText,
            Lower(member.Expression)),
        InvocationExpressionSyntax invocation => LowerInvocationOrElementAccess(invocation),
        UnaryExpressionSyntax unary => OperatorNode(
            DebugExpressionNodeKind.Unary,
            UnaryOperator(unary.Kind()),
            Lower(unary.Operand)),
        BinaryExpressionSyntax binary => OperatorNode(
            DebugExpressionNodeKind.Binary,
            BinaryOperator(binary.Kind()),
            Lower(binary.Left),
            Lower(binary.Right)),
        TernaryConditionalExpressionSyntax conditional => Node(
            DebugExpressionNodeKind.Conditional,
            children:
            [
                Lower(conditional.Condition),
                Lower(conditional.WhenTrue),
                Lower(conditional.WhenFalse)
            ]),
        _ => throw new NotSupportedException(
            $"Visual Basic expression kind {syntax.Kind()} is not supported by safe evaluation.")
    };

    private static DebugExpressionNode LowerInvocationOrElementAccess(
        InvocationExpressionSyntax invocation)
    {
        if (invocation.ArgumentList is null)
        {
            throw new NotSupportedException(
                "Visual Basic invocation without an argument list is not safe array access.");
        }

        if (invocation.Expression is MemberAccessExpressionSyntax member)
        {
            return Node(
                DebugExpressionNodeKind.Invocation,
                member.Name.Identifier.ValueText,
                [
                    Lower(member.Expression),
                    .. invocation.ArgumentList.Arguments.Select(LowerArgument)
                ]);
        }

        return Node(
            DebugExpressionNodeKind.ElementAccess,
            children:
            [
                Lower(invocation.Expression),
                .. invocation.ArgumentList.Arguments.Select(LowerArgument)
            ]);
    }

    private static DebugExpressionNode LowerArgument(ArgumentSyntax argument) => argument switch
    {
        SimpleArgumentSyntax simple => Lower(simple.Expression),
        _ => throw new NotSupportedException(
            "Named and omitted Visual Basic arguments are not supported.")
    };

    private static DebugExpressionOperator UnaryOperator(SyntaxKind kind) => kind switch
    {
        SyntaxKind.UnaryPlusExpression => DebugExpressionOperator.UnaryPlus,
        SyntaxKind.UnaryMinusExpression => DebugExpressionOperator.Negate,
        SyntaxKind.NotExpression => DebugExpressionOperator.LogicalNot,
        _ => throw new NotSupportedException(
            $"Visual Basic unary operator {kind} is not supported by safe evaluation.")
    };

    private static DebugExpressionOperator BinaryOperator(SyntaxKind kind) => kind switch
    {
        SyntaxKind.AddExpression or SyntaxKind.ConcatenateExpression => DebugExpressionOperator.Add,
        SyntaxKind.SubtractExpression => DebugExpressionOperator.Subtract,
        SyntaxKind.MultiplyExpression => DebugExpressionOperator.Multiply,
        SyntaxKind.DivideExpression or SyntaxKind.IntegerDivideExpression =>
            DebugExpressionOperator.Divide,
        SyntaxKind.ModuloExpression => DebugExpressionOperator.Remainder,
        SyntaxKind.EqualsExpression => DebugExpressionOperator.Equal,
        SyntaxKind.NotEqualsExpression => DebugExpressionOperator.NotEqual,
        SyntaxKind.LessThanExpression => DebugExpressionOperator.LessThan,
        SyntaxKind.LessThanOrEqualExpression => DebugExpressionOperator.LessThanOrEqual,
        SyntaxKind.GreaterThanExpression => DebugExpressionOperator.GreaterThan,
        SyntaxKind.GreaterThanOrEqualExpression => DebugExpressionOperator.GreaterThanOrEqual,
        SyntaxKind.AndAlsoExpression => DebugExpressionOperator.LogicalAnd,
        SyntaxKind.OrElseExpression => DebugExpressionOperator.LogicalOr,
        SyntaxKind.AndExpression => DebugExpressionOperator.BitwiseAnd,
        SyntaxKind.OrExpression => DebugExpressionOperator.BitwiseOr,
        SyntaxKind.ExclusiveOrExpression => DebugExpressionOperator.ExclusiveOr,
        _ => throw new NotSupportedException(
            $"Visual Basic binary operator {kind} is not supported by safe evaluation.")
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
}
