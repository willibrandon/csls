using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using System;
using System.Collections.Immutable;
using System.Linq;

namespace Csls.SourceGen;

/// <summary>
/// Prevents implicit sequence-filtering loops reported by CodeQL's missed-where query.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class CodeQlMissedWhereAnalyzer : DiagnosticAnalyzer
{
    /// <summary>
    /// Identifies an iteration filter that must be expressed before the loop.
    /// </summary>
    public const string DiagnosticId = "CSLS0009";

    private static readonly DiagnosticDescriptor s_rule = new(
        DiagnosticId,
        "Filter sequences before iteration",
        "Loop variable '{0}' implicitly filters its sequence; express the filter before iteration",
        "CodeQuality",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "Sequence-filtering loops must not introduce CodeQL cs/linq/missed-where findings.");

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
        context.RegisterSyntaxNodeAction(AnalyzeForEach, SyntaxKind.ForEachStatement);
    }

    private static void AnalyzeForEach(SyntaxNodeAnalysisContext context)
    {
        var statement = (ForEachStatementSyntax)context.Node;
        if (!SupportsGenericLinq(statement, context) ||
            GetStatements(statement.Statement) is not { Count: > 0 } statements ||
            statements[0] is not IfStatementSyntax conditional ||
            !ReferencesIterationVariable(statement, conditional.Condition, context) ||
            IsMissedAllPattern(conditional, statements) ||
            !IsImplicitFilter(conditional, statements))
        {
            return;
        }

        context.ReportDiagnostic(Diagnostic.Create(
            s_rule,
            statement.GetLocation(),
            statement.Identifier.ValueText));
    }

    private static bool SupportsGenericLinq(
        ForEachStatementSyntax statement,
        SyntaxNodeAnalysisContext context)
    {
        ITypeSymbol? type = context.SemanticModel.GetTypeInfo(
            statement.Expression,
            context.CancellationToken).Type;
        return type is IArrayTypeSymbol { Rank: 1 } ||
            type is not null && (IsGenericEnumerable(type) ||
                type.AllInterfaces.Any(IsGenericEnumerable));
    }

    private static bool IsGenericEnumerable(INamedTypeSymbol type) =>
        type.OriginalDefinition.SpecialType ==
            SpecialType.System_Collections_Generic_IEnumerable_T;

    private static bool IsGenericEnumerable(ITypeSymbol type) =>
        type is INamedTypeSymbol named && IsGenericEnumerable(named);

    private static bool ReferencesIterationVariable(
        ForEachStatementSyntax statement,
        ExpressionSyntax condition,
        SyntaxNodeAnalysisContext context)
    {
        ISymbol? iterationVariable = context.SemanticModel.GetDeclaredSymbol(
            statement,
            context.CancellationToken);
        return iterationVariable is not null && condition
            .DescendantNodesAndSelf()
            .OfType<IdentifierNameSyntax>()
            .Any(identifier => SymbolEqualityComparer.Default.Equals(
                iterationVariable,
                context.SemanticModel.GetSymbolInfo(
                    identifier,
                    context.CancellationToken).Symbol));
    }

    private static bool IsImplicitFilter(
        IfStatementSyntax conditional,
        SyntaxList<StatementSyntax> statements) =>
        IsContinue(conditional.Statement) ||
        conditional.Else is null && statements.Count == 1;

    private static bool IsMissedAllPattern(
        IfStatementSyntax conditional,
        SyntaxList<StatementSyntax> statements) =>
        conditional.Else is null && statements.Count == 1 &&
        conditional.Statement.DescendantNodesAndSelf()
            .OfType<BreakStatementSyntax>()
            .Any() &&
        conditional.Statement.DescendantNodesAndSelf()
            .OfType<AssignmentExpressionSyntax>()
            .Any(static assignment => assignment.Right.IsKind(
                SyntaxKind.FalseLiteralExpression));

    private static bool IsContinue(StatementSyntax statement) =>
        statement is ContinueStatementSyntax;

    private static SyntaxList<StatementSyntax> GetStatements(StatementSyntax statement) =>
        statement is BlockSyntax block ? block.Statements : new SyntaxList<StatementSyntax>(statement);
}
