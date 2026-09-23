using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using System;
using System.Collections.Immutable;

namespace Csls.SourceGen;

/// <summary>
/// Prevents deeply branched Boolean expressions reported by CodeQL's complex-condition query.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class CodeQlComplexConditionAnalyzer : DiagnosticAnalyzer
{
    private const int MaximumLogicalGroupCount = 3;

    /// <summary>
    /// Identifies a complex condition that must be decomposed into named decisions.
    /// </summary>
    public const string DiagnosticId = "CSLS0013";

    private static readonly DiagnosticDescriptor s_rule = new(
        DiagnosticId,
        "Decompose complex Boolean conditions",
        "Condition has {0} logical groups; decompose it into named decisions",
        "CodeQuality",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "Conditions must not introduce CodeQL cs/complex-condition findings.");

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
        context.RegisterSyntaxNodeAction(
            AnalyzeExpression,
            SyntaxKind.LogicalAndExpression,
            SyntaxKind.LogicalOrExpression,
            SyntaxKind.BitwiseAndExpression,
            SyntaxKind.BitwiseOrExpression,
            SyntaxKind.ExclusiveOrExpression);
    }

    private static void AnalyzeExpression(SyntaxNodeAnalysisContext context)
    {
        var expression = (BinaryExpressionSyntax)context.Node;
        if (HasRelevantParent(expression))
        {
            return;
        }

        int logicalGroupCount = CountLogicalGroups(expression, parentKind: null);
        if (logicalGroupCount <= MaximumLogicalGroupCount)
        {
            return;
        }

        context.ReportDiagnostic(Diagnostic.Create(
            s_rule,
            expression.GetLocation(),
            logicalGroupCount));
    }

    private static bool HasRelevantParent(ExpressionSyntax expression)
    {
        SyntaxNode? parent = expression.Parent;
        while (parent is ParenthesizedExpressionSyntax)
        {
            parent = parent.Parent;
        }

        return parent is BinaryExpressionSyntax binary && IsRelevant(binary.Kind());
    }

    private static int CountLogicalGroups(ExpressionSyntax expression, SyntaxKind? parentKind)
    {
        while (expression is ParenthesizedExpressionSyntax parenthesized)
        {
            expression = parenthesized.Expression;
        }

        if (expression is not BinaryExpressionSyntax binary || !IsRelevant(binary.Kind()))
        {
            return 0;
        }

        SyntaxKind kind = binary.Kind();
        int currentGroup = kind == parentKind ? 0 : 1;
        return currentGroup +
            CountLogicalGroups(binary.Left, kind) +
            CountLogicalGroups(binary.Right, kind);
    }

    private static bool IsRelevant(SyntaxKind kind) =>
        kind is SyntaxKind.LogicalAndExpression or
            SyntaxKind.LogicalOrExpression or
            SyntaxKind.BitwiseAndExpression or
            SyntaxKind.BitwiseOrExpression or
            SyntaxKind.ExclusiveOrExpression;
}
