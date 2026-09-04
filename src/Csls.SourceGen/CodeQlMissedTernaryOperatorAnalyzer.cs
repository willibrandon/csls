using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using System;
using System.Collections.Immutable;

namespace Csls.SourceGen;

/// <summary>
/// Prevents conditional branches that only assign the same target.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class CodeQlMissedTernaryOperatorAnalyzer : DiagnosticAnalyzer
{
    /// <summary>
    /// Identifies a conditional assignment that should use a conditional expression.
    /// </summary>
    public const string DiagnosticId = "CSLS0020";

    private static readonly DiagnosticDescriptor s_rule = new(
        DiagnosticId,
        "Use a conditional expression",
        "Both branches assign '{0}'; use a conditional expression",
        "CodeQuality",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "Equivalent branch assignments must not introduce CodeQL cs/missed-ternary-operator findings.");

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
        context.RegisterSyntaxNodeAction(AnalyzeIfStatement, SyntaxKind.IfStatement);
    }

    private static void AnalyzeIfStatement(SyntaxNodeAnalysisContext context)
    {
        var statement = (IfStatementSyntax)context.Node;
        if (statement.Else is null ||
            statement.Else.Statement is IfStatementSyntax ||
            !TryGetOnlyAssignment(statement.Statement, out AssignmentExpressionSyntax whenTrue) ||
            !TryGetOnlyAssignment(
                statement.Else.Statement,
                out AssignmentExpressionSyntax whenFalse))
        {
            return;
        }

        ISymbol? trueTarget = context.SemanticModel.GetSymbolInfo(
            whenTrue.Left,
            context.CancellationToken).Symbol;
        ISymbol? falseTarget = context.SemanticModel.GetSymbolInfo(
            whenFalse.Left,
            context.CancellationToken).Symbol;
        if (trueTarget is not ILocalSymbol local ||
            !SymbolEqualityComparer.Default.Equals(local, falseTarget))
        {
            return;
        }

        context.ReportDiagnostic(Diagnostic.Create(
            s_rule,
            statement.GetLocation(),
            local.Name));
    }

    private static bool TryGetOnlyAssignment(
        StatementSyntax statement,
        out AssignmentExpressionSyntax assignment)
    {
        ExpressionStatementSyntax? expression;
        if (statement is ExpressionStatementSyntax direct)
        {
            expression = direct;
        }
        else if (statement is BlockSyntax block &&
            block.Statements.Count == 1 &&
            block.Statements[0] is ExpressionStatementSyntax only)
        {
            expression = only;
        }
        else
        {
            expression = null;
        }

        assignment = expression?.Expression as AssignmentExpressionSyntax ?? null!;
        return assignment is not null &&
            assignment.IsKind(SyntaxKind.SimpleAssignmentExpression);
    }
}
