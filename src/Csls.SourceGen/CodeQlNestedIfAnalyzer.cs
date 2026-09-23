using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using System;
using System.Collections.Immutable;

namespace Csls.SourceGen;

/// <summary>
/// Detects directly nested conditional statements that preserve their behavior when combined.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class CodeQlNestedIfAnalyzer : DiagnosticAnalyzer
{
    /// <summary>
    /// Identifies nested conditions without alternative branches or intervening statements.
    /// </summary>
    public const string DiagnosticId = "CSLS0025";

    private static readonly DiagnosticDescriptor s_rule = new(
        DiagnosticId, "Combine nested conditions", "Combine these nested conditions with short-circuit AND",
        "CodeQuality", DiagnosticSeverity.Error, isEnabledByDefault: true,
        description: "Directly nested conditions must not introduce CodeQL cs/nested-if-statements findings.");

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
        context.RegisterSyntaxNodeAction(AnalyzeIf, SyntaxKind.IfStatement);
    }

    private static void AnalyzeIf(SyntaxNodeAnalysisContext context)
    {
        var outer = (IfStatementSyntax)context.Node;
        StatementSyntax statement = outer.Statement;
        while (statement is BlockSyntax { Statements.Count: 1 } block)
        {
            statement = block.Statements[0];
        }

        if (outer.Else is null && statement is IfStatementSyntax { Else: null } && !outer.ContainsDirectives)
        {
            context.ReportDiagnostic(Diagnostic.Create(s_rule, outer.IfKeyword.GetLocation()));
        }
    }
}
