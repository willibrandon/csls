using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using System;
using System.Collections.Immutable;
using System.Linq;

namespace Csls.SourceGen;

/// <summary>
/// Prevents CodeQL missed-select findings in repository metadata projection loops.
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
        "Project metadata before iteration",
        "Loop variable '{0}' is immediately mapped; project it with Select before iteration",
        "CodeQuality",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "Metadata projection loops must not introduce CodeQL cs/linq/missed-select findings.");

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
        if (statement.Statement is not BlockSyntax { Statements.Count: > 0 } body ||
            body.Statements[0] is not LocalDeclarationStatementSyntax declaration ||
            declaration.Declaration.Variables.Count != 1 ||
            declaration.Declaration.Variables[0].Initializer?.Value is not
                InvocationExpressionSyntax invocation ||
            context.SemanticModel.GetSymbolInfo(invocation, context.CancellationToken).Symbol is not
                IMethodSymbol method ||
            !IsMetadataProjection(method))
        {
            return;
        }

        ISymbol? iterationVariable = context.SemanticModel.GetDeclaredSymbol(
            statement,
            context.CancellationToken);
        if (iterationVariable is null || !invocation
            .DescendantNodesAndSelf()
            .OfType<IdentifierNameSyntax>()
            .Any(identifier => SymbolEqualityComparer.Default.Equals(
                iterationVariable,
                context.SemanticModel.GetSymbolInfo(
                    identifier,
                    context.CancellationToken).Symbol)))
        {
            return;
        }

        context.ReportDiagnostic(Diagnostic.Create(
            s_rule,
            declaration.GetLocation(),
            statement.Identifier.ValueText));
    }

    private static bool IsMetadataProjection(IMethodSymbol method)
    {
        string typeName = method.ContainingType.ToDisplayString();
        return method.Name == "Read" &&
                typeName == "Csls.Debugger.PortablePdbSourceDocumentReader" ||
            method.Name == "FromMetadataImage" &&
                typeName == "System.Reflection.Metadata.MetadataReaderProvider";
    }
}
