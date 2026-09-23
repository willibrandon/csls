using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using System;
using System.Collections.Immutable;
using System.Linq;

namespace Csls.SourceGen;

/// <summary>
/// Rejects unfiltered catch-all handlers that replace or consume the original exception.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class CodeQlGenericCatchAnalyzer : DiagnosticAnalyzer
{
    /// <summary>
    /// Identifies catch-all handling without a filter or original-exception propagation.
    /// </summary>
    public const string DiagnosticId = "CSLS0024";

    private static readonly DiagnosticDescriptor s_rule = new(
        DiagnosticId,
        "Narrow catch-all exception handling",
        "Catch specific exceptions, filter recoverable failures, or rethrow the original exception",
        "Reliability",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "Unfiltered catch-all handlers must not introduce CodeQL cs/catch-of-all-exceptions findings.");

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
        context.RegisterSyntaxNodeAction(AnalyzeCatch, SyntaxKind.CatchClause);
    }

    private static void AnalyzeCatch(SyntaxNodeAnalysisContext context)
    {
        var clause = (CatchClauseSyntax)context.Node;
        if (clause.Filter is not null)
        {
            return;
        }

        if (clause.Declaration is { } declaration && !SymbolEqualityComparer.Default.Equals(
            context.SemanticModel.GetTypeInfo(declaration.Type, context.CancellationToken).Type,
            context.Compilation.GetTypeByMetadataName("System.Exception")))
        {
            return;
        }

        bool rethrowsOriginal = clause.Block.DescendantNodes(static node =>
                node is not (LocalFunctionStatementSyntax or AnonymousFunctionExpressionSyntax or CatchClauseSyntax))
            .OfType<ThrowStatementSyntax>().Any(static statement => statement.Expression is null);
        if (!rethrowsOriginal)
        {
            context.ReportDiagnostic(Diagnostic.Create(s_rule, clause.CatchKeyword.GetLocation()));
        }
    }
}
