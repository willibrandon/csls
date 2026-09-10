using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using System;
using System.Collections.Immutable;

namespace Csls.SourceGen;

/// <summary>
/// Prevents throw-expression conditionals that CodeQL reports as simplifiable Boolean expressions.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class CodeQlSimplifiableBooleanExpressionAnalyzer : DiagnosticAnalyzer
{
    /// <summary>
    /// Identifies Boolean conditionals that must express throwing through statement control flow.
    /// </summary>
    public const string DiagnosticId = "CSLS0010";

    private static readonly DiagnosticDescriptor s_rule = new(
        DiagnosticId,
        "Use statement control flow for conditional throws",
        "Boolean conditional with a throw expression must use statement control flow",
        "CodeQuality",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "Conditional throws must not introduce CodeQL cs/simplifiable-boolean-expression findings.");

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
        context.RegisterSyntaxNodeAction(AnalyzeConditional, SyntaxKind.ConditionalExpression);
    }

    private static void AnalyzeConditional(SyntaxNodeAnalysisContext context)
    {
        var expression = (ConditionalExpressionSyntax)context.Node;
        if (!IsThrowAndBooleanLiteral(expression.WhenTrue, expression.WhenFalse) &&
            !IsThrowAndBooleanLiteral(expression.WhenFalse, expression.WhenTrue))
        {
            return;
        }

        context.ReportDiagnostic(Diagnostic.Create(s_rule, expression.GetLocation()));
    }

    private static bool IsThrowAndBooleanLiteral(
        ExpressionSyntax throwCandidate,
        ExpressionSyntax literalCandidate)
    {
        if (throwCandidate is not ThrowExpressionSyntax)
        {
            return false;
        }

        return literalCandidate.IsKind(SyntaxKind.TrueLiteralExpression) ||
            literalCandidate.IsKind(SyntaxKind.FalseLiteralExpression);
    }
}
