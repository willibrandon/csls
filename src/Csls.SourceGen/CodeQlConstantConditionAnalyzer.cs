using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using System;
using System.Collections.Immutable;
using System.Linq;

namespace Csls.SourceGen;

/// <summary>
/// Prevents null tests made constant by guards or correlated conditional initializers.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class CodeQlConstantConditionAnalyzer : DiagnosticAnalyzer
{
    /// <summary>
    /// Identifies null tests whose result is fixed by an earlier condition.
    /// </summary>
    public const string DiagnosticId = "CSLS0017";

    private static readonly DiagnosticDescriptor s_rule = new(
        DiagnosticId,
        "Remove constant null condition",
        "Null test is always '{0}' after the earlier condition",
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

        if (TryGetCorrelatedNullValue(context, pattern, body, current, testsNull, out bool correlatedValue))
        {
            context.ReportDiagnostic(Diagnostic.Create(
                s_rule,
                pattern.GetLocation(),
                correlatedValue ? "true" : "false"));
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

    private static bool TryGetCorrelatedNullValue(
        SyntaxNodeAnalysisContext context,
        IsPatternExpressionSyntax pattern,
        BlockSyntax body,
        StatementSyntax current,
        bool testsNull,
        out bool value)
    {
        value = false;
        SyntaxNode operand = pattern;
        while (operand.Parent is ParenthesizedExpressionSyntax parentheses)
        {
            operand = parentheses;
        }

        if (operand.Parent is not BinaryExpressionSyntax binary ||
            binary.Right != operand ||
            (!binary.IsKind(SyntaxKind.LogicalAndExpression) && !binary.IsKind(SyntaxKind.LogicalOrExpression)) ||
            UnwrapParentheses(binary.Left) is not IsPatternExpressionSyntax guard ||
            !TryGetNullTest(guard.Pattern, out bool guardTestsNull) ||
            context.SemanticModel.GetSymbolInfo(guard.Expression, context.CancellationToken).Symbol is not ISymbol source ||
            source is not (ILocalSymbol { RefKind: RefKind.None } or IParameterSymbol { RefKind: RefKind.None }) ||
            context.SemanticModel.GetSymbolInfo(pattern.Expression, context.CancellationToken).Symbol is not
                ILocalSymbol { RefKind: RefKind.None } local ||
            local.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax(context.CancellationToken) is not
                VariableDeclaratorSyntax { Initializer.Value: ConditionalExpressionSyntax initializer } declarator ||
            declarator.Parent?.Parent is not LocalDeclarationStatementSyntax declaration ||
            declaration.Parent != body || declaration.Declaration.Variables.Count != 1 ||
            UnwrapParentheses(initializer.Condition) is not IsPatternExpressionSyntax initialGuard ||
            !TryGetNullTest(initialGuard.Pattern, out bool initialTestsNull) ||
            !SymbolEqualityComparer.Default.Equals(source,
                context.SemanticModel.GetSymbolInfo(initialGuard.Expression, context.CancellationToken).Symbol))
        {
            return false;
        }

        int declarationIndex = body.Statements.IndexOf(declaration);
        int currentIndex = body.Statements.IndexOf(current);
        if (currentIndex <= declarationIndex ||
            !CorrelationIsUnchanged(context, body, body.Statements[declarationIndex + 1], current,
                initializer, source, local))
        {
            return false;
        }

        bool sourceIsNull = binary.IsKind(SyntaxKind.LogicalAndExpression) ? guardTestsNull : !guardTestsNull;
        ExpressionSyntax selected = UnwrapParentheses(
            sourceIsNull == initialTestsNull ? initializer.WhenTrue : initializer.WhenFalse);
        if (selected.IsKind(SyntaxKind.NullLiteralExpression))
        {
            value = testsNull;
            return true;
        }

        if (selected is ObjectCreationExpressionSyntax or ImplicitObjectCreationExpressionSyntax &&
            context.SemanticModel.GetTypeInfo(selected, context.CancellationToken).Type is
            { IsReferenceType: true } createdType &&
            context.Compilation.ClassifyCommonConversion(createdType, local.Type) is
        { IsIdentity: true } or { IsReference: true })
        {
            value = !testsNull;
            return true;
        }

        return false;
    }

    private static bool CorrelationIsUnchanged(
        SyntaxNodeAnalysisContext context,
        BlockSyntax body,
        StatementSyntax first,
        StatementSyntax last,
        ConditionalExpressionSyntax initializer,
        ISymbol source,
        ILocalSymbol local)
    {
        DataFlowAnalysis? bodyFlow = context.SemanticModel.AnalyzeDataFlow(body);
        DataFlowAnalysis? initialFlow = context.SemanticModel.AnalyzeDataFlow(initializer);
        DataFlowAnalysis? subsequentFlow = context.SemanticModel.AnalyzeDataFlow(first, last);
        if (bodyFlow is null || !bodyFlow.Succeeded ||
            initialFlow is null || !initialFlow.Succeeded ||
            subsequentFlow is null || !subsequentFlow.Succeeded)
        {
            return false;
        }

        return !bodyFlow.Captured.Any(symbol => IsCorrelatedSymbol(symbol, source, local)) &&
            !bodyFlow.UnsafeAddressTaken.Any(symbol => IsCorrelatedSymbol(symbol, source, local)) &&
            !initialFlow.WrittenInside.Any(symbol => IsCorrelatedSymbol(symbol, source, local)) &&
            !subsequentFlow.WrittenInside.Any(symbol => IsCorrelatedSymbol(symbol, source, local));
    }

    private static bool IsCorrelatedSymbol(ISymbol symbol, ISymbol source, ILocalSymbol local) =>
        SymbolEqualityComparer.Default.Equals(symbol, source) || SymbolEqualityComparer.Default.Equals(symbol, local);

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
