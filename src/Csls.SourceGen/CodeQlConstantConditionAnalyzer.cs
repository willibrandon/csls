using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using System;
using System.Collections.Immutable;

namespace Csls.SourceGen;

/// <summary>
/// Prevents repeated null tests made constant by an earlier exiting guard.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class CodeQlConstantConditionAnalyzer : DiagnosticAnalyzer
{
    /// <summary>
    /// Identifies null tests whose result is fixed by an earlier guard clause.
    /// </summary>
    public const string DiagnosticId = "CSLS0017";

    private static readonly DiagnosticDescriptor s_rule = new(
        DiagnosticId,
        "Remove constant null condition",
        "Null test is always '{0}' after the earlier exiting guard",
        "CodeQuality",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "Repeated null tests must not introduce CodeQL cs/constant-condition findings.");

    /// <inheritdoc />
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [s_rule];

    /// <inheritdoc />
    public override void Initialize(AnalysisContext context)
    {
        if (context is null)
        {
            throw new ArgumentNullException(nameof(context));
        }

        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(AnalyzePattern, SyntaxKind.IsPatternExpression);
    }

    private static void AnalyzePattern(SyntaxNodeAnalysisContext context)
    {
        var pattern = (IsPatternExpressionSyntax)context.Node;
        if (!TryGetNullTest(pattern.Pattern, out bool testsNull) ||
            FindContainingMethodBody(pattern) is not BlockSyntax body ||
            FindContainingTopLevelStatement(pattern, body) is not StatementSyntax current)
        {
            return;
        }

        foreach (StatementSyntax statement in body.Statements)
        {
            if (ReferenceEquals(statement, current))
            {
                return;
            }

            if (statement is not IfStatementSyntax guard ||
                guard.Else is not null ||
                !AlwaysExits(guard.Statement) ||
                UnwrapParentheses(guard.Condition) is not IsPatternExpressionSyntax guardPattern ||
                !TryGetNullTest(guardPattern.Pattern, out bool guardTestsNull) ||
                !SyntaxFactory.AreEquivalent(
                    UnwrapParentheses(guardPattern.Expression),
                    UnwrapParentheses(pattern.Expression)))
            {
                continue;
            }

            bool constantValue = testsNull != guardTestsNull;
            context.ReportDiagnostic(Diagnostic.Create(
                s_rule,
                pattern.GetLocation(),
                constantValue ? "true" : "false"));
            return;
        }
    }

    private static BlockSyntax? FindContainingMethodBody(SyntaxNode node)
    {
        for (SyntaxNode? current = node.Parent; current is not null; current = current.Parent)
        {
            if (current is BaseMethodDeclarationSyntax method)
            {
                return method.Body;
            }

            if (current is LocalFunctionStatementSyntax localFunction)
            {
                return localFunction.Body;
            }

            if (current is AnonymousFunctionExpressionSyntax or AccessorDeclarationSyntax)
            {
                return null;
            }
        }

        return null;
    }

    private static StatementSyntax? FindContainingTopLevelStatement(
        SyntaxNode node,
        BlockSyntax body)
    {
        for (SyntaxNode? current = node; current is not null && current != body;
            current = current.Parent)
        {
            if (current is StatementSyntax statement && statement.Parent == body)
            {
                return statement;
            }
        }

        return null;
    }

    private static bool AlwaysExits(StatementSyntax statement)
    {
        if (statement is ReturnStatementSyntax or ThrowStatementSyntax)
        {
            return true;
        }

        return statement is BlockSyntax block &&
            block.Statements.Count > 0 &&
            AlwaysExits(block.Statements[block.Statements.Count - 1]);
    }

    private static bool TryGetNullTest(PatternSyntax pattern, out bool testsNull)
    {
        if (pattern is ConstantPatternSyntax constant &&
            constant.Expression.IsKind(SyntaxKind.NullLiteralExpression))
        {
            testsNull = true;
            return true;
        }

        if (pattern is UnaryPatternSyntax unary &&
            unary.IsKind(SyntaxKind.NotPattern) &&
            unary.Pattern is ConstantPatternSyntax negated &&
            negated.Expression.IsKind(SyntaxKind.NullLiteralExpression))
        {
            testsNull = false;
            return true;
        }

        testsNull = false;
        return false;
    }

    private static ExpressionSyntax UnwrapParentheses(ExpressionSyntax expression)
    {
        while (expression is ParenthesizedExpressionSyntax parenthesized)
        {
            expression = parenthesized.Expression;
        }

        return expression;
    }
}
