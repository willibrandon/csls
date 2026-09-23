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

    private static DebugExpressionNode Lower(
        ExpressionSyntax syntax,
        bool checkedContext = false) => syntax switch
        {
            IdentifierNameSyntax identifier => Node(
                DebugExpressionNodeKind.Identifier,
                identifier.Identifier.ValueText),
            ThisExpressionSyntax => Node(DebugExpressionNodeKind.This),
            LiteralExpressionSyntax literal when literal.IsKind(SyntaxKind.DefaultLiteralExpression) =>
                Node(DebugExpressionNodeKind.DefaultLiteral),
            DefaultExpressionSyntax typedDefault => ConversionNode(
                typedDefault.Type.ToString(),
                Node(DebugExpressionNodeKind.DefaultLiteral),
                checkedContext),
            LiteralExpressionSyntax literal => ExpressionLiteral.Create(literal.Token.Value),
            ParenthesizedExpressionSyntax parenthesized => Lower(
                parenthesized.Expression,
                checkedContext),
            CheckedExpressionSyntax checkedExpression => Lower(
                checkedExpression.Expression,
                checkedExpression.IsKind(SyntaxKind.CheckedExpression)),
            CastExpressionSyntax conversion => ConversionNode(
                conversion.Type.ToString(),
                Lower(conversion.Expression, checkedContext),
                checkedContext),
            BinaryExpressionSyntax binary when binary.IsKind(SyntaxKind.IsExpression) ||
                binary.IsKind(SyntaxKind.AsExpression) => new DebugExpressionNode(
                    binary.IsKind(SyntaxKind.IsExpression) ? DebugExpressionNodeKind.TypeTest : DebugExpressionNodeKind.TryCast,
                    DebugExpressionOperator.None, Text: null, binary.Right.ToString(),
                    [Lower(binary.Left, checkedContext)]),
            MemberAccessExpressionSyntax member
                when member.IsKind(SyntaxKind.SimpleMemberAccessExpression) => Node(
                    DebugExpressionNodeKind.MemberAccess,
                    member.Name.Identifier.ValueText,
                    Lower(member.Expression, checkedContext)),
            ElementAccessExpressionSyntax element => Node(
                DebugExpressionNodeKind.ElementAccess,
                children:
                [
                    Lower(element.Expression, checkedContext),
                .. element.ArgumentList.Arguments.Select(argument =>
                    Lower(argument.Expression, checkedContext))
                ]),
            ObjectCreationExpressionSyntax creation => LowerObjectCreation(creation, checkedContext),
            InvocationExpressionSyntax invocation => LowerInvocation(invocation, checkedContext),
            PrefixUnaryExpressionSyntax unary => OperatorNode(
                DebugExpressionNodeKind.Unary,
                UnaryOperator(unary.Kind(), checkedContext),
                Lower(unary.Operand, checkedContext)),
            BinaryExpressionSyntax binary => OperatorNode(
                DebugExpressionNodeKind.Binary,
                BinaryOperator(binary.Kind(), checkedContext),
                Lower(binary.Left, checkedContext),
                Lower(binary.Right, checkedContext)),
            ConditionalExpressionSyntax conditional => Node(
                DebugExpressionNodeKind.Conditional,
                children:
                [
                    Lower(conditional.Condition, checkedContext),
                Lower(conditional.WhenTrue, checkedContext),
                Lower(conditional.WhenFalse, checkedContext)
                ]),
            _ => throw new NotSupportedException(
                $"C# expression kind {syntax.Kind()} is not supported by safe evaluation.")
        };

    private static DebugExpressionNode LowerObjectCreation(
        ObjectCreationExpressionSyntax creation,
        bool checkedContext)
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
                .Select(argument => LowerArgument(argument, checkedContext))
                .ToArray() ?? []);
    }

    private static DebugExpressionNode LowerInvocation(
        InvocationExpressionSyntax invocation,
        bool checkedContext)
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
                Lower(member.Expression, checkedContext),
                .. invocation.ArgumentList.Arguments.Select(argument =>
                    LowerArgument(argument, checkedContext))
            ]);
    }

    private static DebugExpressionNode LowerArgument(
        ArgumentSyntax argument,
        bool checkedContext) =>
        argument.NameColon is { } name
            ? Node(DebugExpressionNodeKind.NamedArgument,
                name.Name.Identifier.ValueText,
                Lower(argument.Expression, checkedContext))
            : Lower(argument.Expression, checkedContext);

    private static DebugExpressionOperator UnaryOperator(
        SyntaxKind kind,
        bool checkedContext) => kind switch
        {
            SyntaxKind.UnaryPlusExpression => DebugExpressionOperator.UnaryPlus,
            SyntaxKind.UnaryMinusExpression => checkedContext
                ? DebugExpressionOperator.CheckedNegate
                : DebugExpressionOperator.Negate,
            SyntaxKind.LogicalNotExpression => DebugExpressionOperator.LogicalNot,
            SyntaxKind.BitwiseNotExpression => DebugExpressionOperator.OnesComplement,
            _ => throw new NotSupportedException(
                $"C# unary operator {kind} is not supported by safe evaluation.")
        };

    private static DebugExpressionOperator BinaryOperator(
        SyntaxKind kind,
        bool checkedContext) => kind switch
        {
            SyntaxKind.AddExpression => checkedContext
                ? DebugExpressionOperator.CheckedAdd
                : DebugExpressionOperator.Add,
            SyntaxKind.SubtractExpression => checkedContext
                ? DebugExpressionOperator.CheckedSubtract
                : DebugExpressionOperator.Subtract,
            SyntaxKind.MultiplyExpression => checkedContext
                ? DebugExpressionOperator.CheckedMultiply
                : DebugExpressionOperator.Multiply,
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
        DebugExpressionNode operand,
        bool checkedContext = false) => new(
            DebugExpressionNodeKind.Conversion,
            checkedContext
                ? DebugExpressionOperator.CheckedConversion
                : DebugExpressionOperator.None,
            Text: null,
            typeName,
            [operand]);
}
