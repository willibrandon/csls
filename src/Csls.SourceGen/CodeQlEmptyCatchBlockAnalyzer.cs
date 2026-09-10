using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using System;
using System.Collections.Immutable;
using System.Linq;

namespace Csls.SourceGen;

/// <summary>
/// Prevents empty exception handlers from silently discarding failures.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class CodeQlEmptyCatchBlockAnalyzer : DiagnosticAnalyzer
{
    /// <summary>
    /// Identifies an exception handler without executable recovery or propagation.
    /// </summary>
    public const string DiagnosticId = "CSLS0021";

    private static readonly DiagnosticDescriptor s_rule = new(
        DiagnosticId,
        "Handle caught exceptions explicitly",
        "Catch block must recover from or propagate the caught exception",
        "Reliability",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "Empty exception handlers must not introduce CodeQL cs/empty-catch-block findings.");

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
        if (clause.Block.DescendantNodes(static node => node is not LocalFunctionStatementSyntax)
            .OfType<StatementSyntax>().Any(static statement =>
                statement is not (EmptyStatementSyntax or BlockSyntax or LocalFunctionStatementSyntax)))
        {
            return;
        }

        context.ReportDiagnostic(Diagnostic.Create(s_rule, clause.CatchKeyword.GetLocation()));
    }
}
