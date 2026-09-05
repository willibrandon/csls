using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using System;
using System.Collections.Immutable;
using System.Linq;

namespace Csls.SourceGen;

/// <summary>
/// Prevents immediate loop-variable projections reported by CodeQL's missed-select query.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class CodeQlMissedSelectAnalyzer : DiagnosticAnalyzer
{
    /// <summary>
    /// Identifies a metadata projection that must be expressed with Select.
    /// </summary>
    public const string DiagnosticId = "CSLS0005";

    private static readonly DiagnosticDescriptor s_rule = new(
        DiagnosticId,
        "Project values before iteration",
        "Loop variable '{0}' is immediately mapped; project it with Select before iteration",
        "CodeQuality",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "Immediate loop-variable projections must not introduce CodeQL cs/linq/missed-select findings.");

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
            statement.Statement is not BlockSyntax { Statements.Count: > 0 } body ||
            body.Statements[0] is not LocalDeclarationStatementSyntax declaration ||
            declaration.Declaration.Variables.Count != 1 ||
            declaration.Declaration.Variables[0].Initializer?.Value is not
                ExpressionSyntax initializer ||
            initializer is CastExpressionSyntax ||
            initializer.DescendantNodesAndSelf().OfType<AwaitExpressionSyntax>().Any() ||
            IsOversized(declaration))
        {
            return;
        }

        ISymbol? iterationVariable = context.SemanticModel.GetDeclaredSymbol(
            statement,
            context.CancellationToken);
        if (iterationVariable is null ||
            !ReferencesSymbol(initializer, iterationVariable, context) ||
            body.Statements.Skip(1).Any(subsequent =>
                ReferencesSymbol(subsequent, iterationVariable, context)))
        {
            return;
        }

        context.ReportDiagnostic(Diagnostic.Create(
            s_rule,
            declaration.GetLocation(),
            statement.Identifier.ValueText));
    }

    private static bool IsOversized(LocalDeclarationStatementSyntax declaration)
    {
        FileLinePositionSpan span = declaration.GetLocation().GetLineSpan();
        return span.EndLinePosition.Character - span.StartLinePosition.Character > 65;
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

    private static bool ReferencesSymbol(
        SyntaxNode node,
        ISymbol symbol,
        SyntaxNodeAnalysisContext context) =>
        node.DescendantNodesAndSelf()
            .OfType<IdentifierNameSyntax>()
            .Any(identifier => SymbolEqualityComparer.Default.Equals(
                symbol,
                context.SemanticModel.GetSymbolInfo(
                    identifier,
                    context.CancellationToken).Symbol));
}
