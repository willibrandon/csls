using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using System;
using System.Collections.Immutable;

namespace Csls.SourceGen;

/// <summary>
/// Prevents null-dereference failures from being used as an exception-based control flow condition.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class CodeQlNullReferenceCatchAnalyzer : DiagnosticAnalyzer
{
    /// <summary>
    /// Identifies catch clauses for the runtime's null-reference exception type.
    /// </summary>
    public const string DiagnosticId = "CSLS0032";

    private static readonly DiagnosticDescriptor s_rule = new(
        DiagnosticId,
        "Validate references before dereferencing",
        "Validate nullable references before use instead of catching NullReferenceException",
        "Reliability",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "Null-reference handlers must not introduce CodeQL cs/catch-nullreferenceexception findings.");

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
        context.RegisterCompilationStartAction(compilation =>
        {
            INamedTypeSymbol? exception = compilation.Compilation.GetTypeByMetadataName("System.NullReferenceException");
            if (exception is not null)
            {
                compilation.RegisterSyntaxNodeAction(node => AnalyzeCatch(node, exception), SyntaxKind.CatchClause);
            }
        });
    }

    private static void AnalyzeCatch(SyntaxNodeAnalysisContext context, INamedTypeSymbol exception)
    {
        var clause = (CatchClauseSyntax)context.Node;
        if (clause.Declaration is { } declaration && SymbolEqualityComparer.Default.Equals(exception,
            context.SemanticModel.GetTypeInfo(declaration.Type, context.CancellationToken).Type))
        {
            context.ReportDiagnostic(Diagnostic.Create(s_rule, clause.CatchKeyword.GetLocation()));
        }
    }
}
