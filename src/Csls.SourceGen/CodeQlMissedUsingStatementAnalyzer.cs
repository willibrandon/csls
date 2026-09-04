using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using System;
using System.Collections.Immutable;

namespace Csls.SourceGen;

/// <summary>
/// Prevents manual metadata-provider disposal that CodeQL requires using syntax for.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class CodeQlMissedUsingStatementAnalyzer : DiagnosticAnalyzer
{
    /// <summary>
    /// Identifies metadata-provider cleanup that must use structured ownership.
    /// </summary>
    public const string DiagnosticId = "CSLS0006";

    private static readonly DiagnosticDescriptor s_rule = new(
        DiagnosticId,
        "Use structured metadata-provider ownership",
        "Metadata reader provider '{0}' is manually disposed in a finally loop",
        "Reliability",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "Metadata reader providers must not introduce CodeQL cs/missed-using-statement findings.");

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
        if (statement.FirstAncestorOrSelf<FinallyClauseSyntax>() is null ||
            context.SemanticModel.GetDeclaredSymbol(
                statement,
                context.CancellationToken) is not ILocalSymbol local ||
            local.Type.ToDisplayString() !=
                "System.Reflection.Metadata.MetadataReaderProvider" ||
            GetSingleStatement(statement.Statement) is not ExpressionStatementSyntax
            {
                Expression: InvocationExpressionSyntax
                {
                    ArgumentList.Arguments.Count: 0,
                    Expression: MemberAccessExpressionSyntax
                    {
                        Name.Identifier.ValueText: "Dispose",
                        Expression: IdentifierNameSyntax receiver
                    }
                }
            } ||
            !SymbolEqualityComparer.Default.Equals(
                local,
                context.SemanticModel.GetSymbolInfo(
                    receiver,
                    context.CancellationToken).Symbol))
        {
            return;
        }

        context.ReportDiagnostic(Diagnostic.Create(
            s_rule,
            statement.GetLocation(),
            statement.Identifier.ValueText));
    }

    private static StatementSyntax? GetSingleStatement(StatementSyntax statement) =>
        statement is BlockSyntax { Statements.Count: 1 } block
            ? block.Statements[0]
            : statement;
}
